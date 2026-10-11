#!/usr/bin/env python3
"""Characterize foam storage precision; this is a diagnostic, not a rendering fix.

Read the actual decay helper, history multiplication and final store expression.
Evaluate their isolated, no-source/no-flow case with binary32 assignment storage,
then apply one of three texture-storage models: RFloat, RHalf round-toward-zero
(RTZ), or a counterfactual RHalf round-to-nearest-even (RNE).

The RTZ model reproduces the published 2026-10-11 Windows D3D11/GTX 1650 results
for commit 90a93293. It is not a forced or universal GPU rounding mode. The CPU
model does not emulate texture interpolation, shader exp/FMA accuracy, dispatch
timing, native half arithmetic, sources, blending, or GPU readback conversions.
No production shader, simulation format, or precision threshold is changed.

References:
https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm
  Sections 3.2.2 (float conversion), 22.4.9 (typed UAV format conversion).
https://docs.vulkan.org/spec/latest/chapters/fundamentals.html
  Floating-Point Format Conversions do not specify a universal rounding mode.

Use --results <editor-results.txt> to check another copy of the same 0.8/s,
initial-value-1, two-second GPU fixture. Use --ref to read earlier shader source.
"""
import argparse
from decimal import Decimal
import math
from pathlib import Path
import re
import struct
import sys
import unittest

import check_foam_quality as source

COMPUTE = source.SHADERS + "Resources/UpdateFoam.compute"
FREQUENCIES = (15, 30, 60, 200)
# Recorded decimal output, not raw GPU float/half bit dumps. These measurements
# are evidence for the model, never cross-device acceptance thresholds.
RECORDED = {
    "RFloat": {15: "0.2018963", 30: "0.2018964", 60: "0.2018958", 200: "0.2018954"},
    "RHalf": {15: "0.2000732", 30: "0.1981201", 60: "0.1939697", 200: "0.1762695"},
}


def binary32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def half_nearest(value):
    return struct.unpack("<e", struct.pack("<e", value))[0]


def half_toward_zero(value):
    """Positive binary16 RTZ: choose the lower representable neighbour."""
    if value < 0 or not math.isfinite(value):
        raise ValueError("This foam-storage model requires nonnegative finite values")
    nearest = half_nearest(value)
    if nearest > value:
        bits = struct.unpack("<H", struct.pack("<e", nearest))[0]
        return struct.unpack("<e", struct.pack("<H", bits - 1))[0]
    return nearest


STORAGE = {"RFloat": binary32, "RHalf-RTZ": half_toward_zero, "RHalf-RNE": half_nearest}


class Binary32Values(dict):
    """Round declared scalar/vector assignments, not every shader instruction."""
    def __init__(self, **values):
        super().__init__()
        for name, value in values.items():
            self[name] = value

    def __setitem__(self, name, value):
        if isinstance(value, (int, float)):
            value = binary32(value)
        elif isinstance(value, tuple):
            value = source.Vec(binary32(component) for component in value)
        super().__setitem__(name, value)


class FoamStorageModel:
    def __init__(self):
        compute = source.source(COMPUTE)
        self.integration = source.body(compute, "CrestFoamDecayAndSource")
        updates = re.findall(r"\bfoam\s*\*=\s*[^;]+;", compute)
        if len(updates) != 1:
            raise AssertionError("Expected one foam-history decay assignment")
        self.update = updates[0]
        self.store = re.search(r"_LD_TexArray_Target\[id\]\s*=\s*([^;]+);", compute)[1]

    def weights(self, dt, rate):
        # The decay path contains one multiply and exp followed by float storage.
        # Host exp rounded to binary32 is a model, not a guarantee of GPU exp bits.
        return source.execute(self.integration, Binary32Values(deltaTime=dt, fadeRate=rate))

    def run(self, frequency, rate=.8, duration=2, initial=1, storage="RHalf-RTZ"):
        steps = round(frequency * duration)
        if not math.isclose(steps, frequency * duration, abs_tol=1e-10):
            raise ValueError("Use a duration containing an integer number of steps")
        quantize = STORAGE[storage]
        values = Binary32Values(foam=quantize(binary32(initial)), decayAndSource=self.weights(1 / frequency, rate))
        for _ in range(steps):
            source.execute(self.update, values)
            values["foam"] = quantize(binary32(source.evaluate(self.store, values)))
        return values["foam"]


def read_results(path):
    """Extract the eight scalar readbacks; do not infer unlogged GPU details."""
    text = Path(path).read_text(encoding="utf-8-sig")
    result = {"RFloat": {}, "RHalf": {}}
    pattern = r"Production foam decay[^\r\n]*?\b(RFloat|RHalf),\s*(\d+) Hz,\s*2 seconds:\s*([0-9.eE+\-]+);"
    for format_name, frequency, value in re.findall(pattern, text):
        frequency = int(frequency)
        if frequency in result[format_name]:
            raise ValueError(f"Duplicate {format_name} {frequency} Hz result")
        result[format_name][frequency] = value
    if any(set(result[name]) != set(FREQUENCIES) for name in result):
        raise ValueError("Expected RFloat and RHalf readbacks at 15/30/60/200 Hz over two seconds")
    return result


def printed_tolerance(decimal_text):
    # The report rounds to decimal digits; matching them does not establish that
    # an unprovided raw GPU buffer is bit-identical to the CPU calculation.
    return .5 * 10 ** Decimal(decimal_text).as_tuple().exponent


class FoamStorageDiagnostics(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.model = FoamStorageModel()

    def test_float32_model_matches_recorded_windows_readbacks(self):
        for frequency, recorded in RECORDED["RFloat"].items():
            actual = self.model.run(frequency, storage="RFloat")
            self.assertAlmostEqual(actual, float(recorded), delta=printed_tolerance(recorded), msg=frequency)

    def test_rtz_half_model_matches_all_four_recorded_windows_readbacks(self):
        for frequency, recorded in RECORDED["RHalf"].items():
            actual = self.model.run(frequency, storage="RHalf-RTZ")
            self.assertAlmostEqual(actual, float(recorded), delta=printed_tolerance(recorded), msg=frequency)

    def test_quantizers_preserve_values_and_select_correct_neighbours(self):
        # Exhaust all nonnegative half values in [0,1], including subnormals,
        # binade boundaries and exact midpoint ties. This checks the rounding
        # model independently of the exponential-decay observations.
        for bits in range(0x3C00 + 1):
            value = struct.unpack("<e", struct.pack("<H", bits))[0]
            self.assertEqual(half_nearest(value), value)
            self.assertEqual(half_toward_zero(value), value)
            if bits == 0x3C00:
                continue
            upper = struct.unpack("<e", struct.pack("<H", bits + 1))[0]
            for fraction in (.25, .5, .75):
                between = binary32(value + fraction * (upper - value))
                self.assertEqual(half_toward_zero(between), value)
                expected = value if fraction < .5 or (fraction == .5 and bits % 2 == 0) else upper
                self.assertEqual(half_nearest(between), expected)

    def test_final_readback_rounding_alone_does_not_reproduce_the_drift(self):
        expected = math.exp(-1.6)
        for frequency in FREQUENCIES:
            full = self.model.run(frequency, storage="RFloat")
            once = half_toward_zero(full)
            repeated = self.model.run(frequency, storage="RHalf-RTZ")
            self.assertLess(abs(once / expected - 1), .001)
            self.assertLess(repeated, once)
        self.assertGreater(abs(self.model.run(200, storage="RHalf-RTZ") / expected - 1), .12)

    def test_analytic_substep_equivalence_does_not_imply_storage_equivalence(self):
        expected = math.exp(-1.6)
        for frequency in FREQUENCIES:
            decay = source.execute(self.model.integration, {"deltaTime": 1 / frequency, "fadeRate": .8}).x
            self.assertAlmostEqual(decay ** (2 * frequency), expected, delta=1e-13)
        storage_values = [self.model.run(frequency) for frequency in FREQUENCIES]
        self.assertTrue(all(first > second for first, second in zip(storage_values, storage_values[1:])))

    def test_nearest_even_can_freeze_slow_fading_instead_of_fixing_lifetime(self):
        # This is intentionally a counterexample, not a recommendation to change
        # production rounding. A sub-half-ULP decrement can round back to itself.
        actual = self.model.run(200, rate=.02, duration=2, storage="RHalf-RNE")
        self.assertEqual(actual, 1)
        self.assertLess(math.exp(-.02 * 2), .97)
        stalled = self.model.run(200, rate=.05, duration=20, storage="RHalf-RNE")
        self.assertEqual(stalled, .9765625)
        self.assertGreater(stalled / math.exp(-.05 * 20), 2.6)

    def test_zero_fade_and_zero_time_preserve_existing_stored_coverage(self):
        for storage, quantize in STORAGE.items():
            for initial in (0, 2 ** -24, 2 ** -14, .01, .15, .5, 1):
                stored = quantize(binary32(initial))
                self.assertEqual(self.model.run(30, rate=0, initial=stored, storage=storage), stored)
                values = Binary32Values(foam=stored, decayAndSource=self.model.weights(0, .8))
                source.execute(self.model.update, values)
                self.assertEqual(quantize(source.evaluate(self.model.store, values)), stored)


def print_characterization():
    model = FoamStorageModel()
    expected = math.exp(-1.6)
    print(f"Initial 1, fade 0.8/s, duration 2s; analytic result {expected:.12f}")
    print(" Hz | float32 result (error %) | half RTZ result (error %) | half RNE result (error %)")
    for frequency in FREQUENCIES:
        values = [model.run(frequency, storage=storage) for storage in STORAGE]
        print(f"{frequency:3d} | " + " | ".join(f"{value:.10f} ({100 * (value / expected - 1):+.6f})" for value in values))
    print("RNE is a counterfactual: fade 0.02/s at 200 Hz remains 1 after 2s; analytic 0.9607894392.")
    print("PASS means the source/storage characterization matches its stated model and recorded fixture, not that RHalf lifetime is accurate.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read shader source at a Git revision")
    parser.add_argument("--results", type=Path, help="Read the same fixture's eight measurements from editor-results.txt")
    args, remaining = parser.parse_known_args()
    source.SOURCE_REF = args.ref
    if args.results:
        RECORDED = read_results(args.results)
    print("Foam storage characterization: CPU source-expression and quantization models; no new Unity/GPU execution.")
    print_characterization()
    unittest.main(argv=[sys.argv[0], *remaining])
