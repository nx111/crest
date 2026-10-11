// CPU adapters for the actual UpdateDynWaves kernel and coordinate helpers.
// Sampling is deterministic and synthetic: no Unity, GPU filtering or half emulation.
#include <algorithm>
#include <cmath>
#include <functional>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

struct float2
{
    float x, y;
    float2(float a = 0) : x(a), y(a) {}
    float2(float a, float b) : x(a), y(b) {}
};
float2 operator+(float2 a, float2 b) { return {a.x + b.x, a.y + b.y}; }
float2 operator-(float2 a, float2 b) { return {a.x - b.x, a.y - b.y}; }
float2 operator*(float2 a, float2 b) { return {a.x * b.x, a.y * b.y}; }
float2 operator/(float2 a, float2 b) { return {a.x / b.x, a.y / b.y}; }
struct float3
{
    float x, y, z;
    float2 xy;
    float3(float a = 0, float b = 0, float c = 0) : x(a), y(b), z(c), xy(a, b) {}
    float3(float2 a, float c) : float3(a.x, a.y, c) {}
};
float3 operator-(float3 a, float3 b) { return {a.x-b.x, a.y-b.y, a.z-b.z}; }
float3 operator+(float3 a, float3 b) { return {a.x+b.x, a.y+b.y, a.z+b.z}; }
float3 operator*(float a, float3 b) { return {a*b.x, a*b.y, a*b.z}; }
struct float4
{
    float x, y, z, w;
    float2 xy;
    float4(float a = 0, float b = 0, float c = 0, float d = 0) : x(a), y(b), z(c), w(d), xy(a,b) {}
};
struct uint3
{
    unsigned x, y, z;
    float2 xy;
    uint3(unsigned a, unsigned b, unsigned c) : x(a), y(b), z(c), xy(float(a),float(b)) {}
};
using half2 = float2;
using std::isfinite;
struct bool2 { bool x, y; };
bool2 isfinite(float2 value) { return {std::isfinite(value.x), std::isfinite(value.y)}; }
bool all(bool2 value) { return value.x && value.y; }
float min(float a, float b) { return std::fmin(a, b); }
float max(float a, float b) { return std::fmax(a, b); }
float2 min(float2 a, float2 b) { return {min(a.x,b.x), min(a.y,b.y)}; }
float saturate(float x) { return std::clamp(x,0.f,1.f); }
float lerp(float a, float b, float t) { return a + (b-a)*t; }

struct CascadeParams
{
    float2 _posSnapped;
    float _texelWidth = 1.f, _textureRes = 16.f, _oneOverTextureRes = 1.f/16.f;
};
struct Cascades
{
    CascadeParams entries[16];
    CascadeParams& operator[](float i) { return entries[int(i)]; }
};
Cascades _CrestCascadeData, _CrestCascadeDataSource;
float3 _OceanCenterPosWorld;

struct TextureArray
{
    float width = 16, height = 16, depth = 1;
    std::function<float4(float3)> sample;
    void GetDimensions(float& w, float& h, float& d) { w=width; h=height; d=depth; }
    float4 SampleLevel(int, float3 uv, float)
    {
        // Linear-clamp fixture coordinates. Tests either sample texel centres,
        // a uniform field, or an affine field, independent of a GPU's sampler.
        return sample({std::clamp(uv.x, .5f/width, 1.f-.5f/width),
                       std::clamp(uv.y, .5f/height, 1.f-.5f/height), uv.z});
    }
};
TextureArray _LD_TexArray_DynamicWaves_Source, _LD_TexArray_SeaFloorDepth, _LD_TexArray_Flow;
template<class T> struct RWTexture2DArray
{
    T value;
    T& operator[](uint3) { return value; }
};
constexpr int LODData_linear_clamp_sampler = 0;
#define SampleLod(texture, position) ((texture).SampleLevel(LODData_linear_clamp_sampler, (position), 0.f))
#define CBUFFER_START(name)
#define CBUFFER_END
#define CREST_FLOAT_MAXIMUM 3.402823466e+38f
#include "OceanCoordinates.hlsl"
#include "UpdateDynWaves.hlsl"

static void check(bool condition, const std::string& message)
{
    if (!condition) throw std::runtime_error(message);
}
static void near(float actual, float expected, const std::string& label, float tolerance = 2e-6f)
{
    check(std::isfinite(actual) && std::abs(actual-expected) <= tolerance,
          label + ": expected " + std::to_string(expected) + ", got " + std::to_string(actual));
}
static void near(float2 actual, float2 expected, const std::string& label)
{
    near(actual.x, expected.x, label+" height"); near(actual.y, expected.y, label+" velocity");
}
static void reset()
{
    _CrestCascadeData = {}; _CrestCascadeDataSource = {};
    _OceanCenterPosWorld = {};
    _Damping=.05f; _Gravity=9.81f; _SimDeltaTime=0; _LODChange=0; _CourantNumber=.7f; _AttenuationInShallows=1;
    _LD_TexArray_DynamicWaves_Source.sample = [](float3) { return float4(1.25f,-.4f); };
    _LD_TexArray_SeaFloorDepth.sample = [](float3) { return float4(-8.f,0.f); };
    _LD_TexArray_Flow.sample = [](float3) { return float4(0.f,0.f); };
}
static float2 run(unsigned x, unsigned y)
{
    _LD_TexArray_Target.value = {999.f,999.f};
    UpdateDynWaves({x,y,0});
    return _LD_TexArray_Target.value;
}
static float edgeWeight(float x, float y)
{
    auto edge = std::min(std::min(x,1.f-x), std::min(y,1.f-y));
    return .95f+.05f*std::clamp(edge/.1f,0.f,1.f);
}
static void zeroStepEdges()
{
    for (auto point : std::vector<std::pair<unsigned,unsigned>>{{0,0},{0,8},{15,0},{15,8},{8,15},{15,15}})
        near(run(point.first, point.second), {1.25f,-.4f}, "zero-step border reprojection");
}
static void repeatedPause()
{
    auto current = float2(1.25f,-.4f);
    for (int frame=0; frame<120; frame++)
    {
        _LD_TexArray_DynamicWaves_Source.sample = [current](float3) { return float4(current.x,current.y); };
        current = run(0,8);
    }
    near(current, {1.25f,-.4f}, "120 paused frames");
}
static void cameraReprojection()
{
    _CrestCascadeData[0]._posSnapped = {-.2f,.125f};
    _LD_TexArray_Flow.sample = [](float3) { return float4(400.f,-100.f); };
    _LD_TexArray_DynamicWaves_Source.sample = [](float3 uv) { return float4(.4f+.6f*uv.x+.2f*uv.y,.1f*uv.x-.2f*uv.y); };
    auto u = std::clamp(.5f/16.f-.2f/16.f,.5f/16.f,1.f-.5f/16.f);
    auto v = (8.5f+.125f)/16.f;
    near(run(0,8), {.4f+.6f*u+.2f*v,.1f*u-.2f*v}, "camera-motion reprojection with no physical time");
}
static void dryLand()
{
    _LD_TexArray_SeaFloorDepth.sample = [](float3) { return float4(1.f,0.f); };
    near(run(0,8), {}, "new dry land still clears history");
}
static void invalidCascade()
{
    _LODChange=-1; near(run(0,8), {}, "below cascade range");
    _LODChange=1; near(run(0,8), {}, "above cascade range");
}
static void invalidHistory()
{
    for (auto invalid : {std::numeric_limits<float>::infinity(), std::numeric_limits<float>::quiet_NaN()})
    {
        _LD_TexArray_DynamicWaves_Source.sample = [invalid](float3) { return float4(1.f,invalid); };
        near(run(0,8), {}, "nonfinite velocity guard");
        _LD_TexArray_DynamicWaves_Source.sample = [invalid](float3) { return float4(invalid,1.f); };
        near(run(0,8), {}, "nonfinite height guard");
    }
}
static void positiveUniform()
{
    _SimDeltaTime=1.f/60.f;
    auto weight=edgeWeight(.5f/16.f,8.5f/16.f);
    auto velocity=-.4f*(1.f-_Damping*_SimDeltaTime)*weight;
    near(run(0,8), {(1.25f+_SimDeltaTime*velocity)*weight,velocity}, "unchanged positive-time edge damping");
}
static void positiveLaplacian()
{
    _SimDeltaTime=1.f/30.f;
    _LD_TexArray_DynamicWaves_Source.sample = [](float3 uv) { return float4(uv.x*uv.x+uv.y*uv.y,.3f); };
    auto u=8.5f/16.f;
    auto c=std::min(std::sqrt(2.f*9.81f*.15915494f),.7f/_SimDeltaTime);
    auto velocity=(.3f+_SimDeltaTime*c*c*4.f/(16.f*16.f))*(1.f-_Damping*_SimDeltaTime);
    near(run(8,8), {2.f*u*u+_SimDeltaTime*velocity,velocity}, "unchanged positive-time Laplacian");
}
static void positiveShallows()
{
    _SimDeltaTime=1.f/15.f;
    _LD_TexArray_SeaFloorDepth.sample = [](float3) { return float4(-.25f,0.f); };
    auto velocity=-.4f*(1.f-_Damping*_SimDeltaTime);
    auto depthMultiplier=1.f-(1.f-.25f)*_SimDeltaTime*2.f;
    near(run(8,8), {(1.25f+_SimDeltaTime*velocity)*depthMultiplier,velocity}, "unchanged positive-time shallow attenuation");
}
int main()
{
    std::vector<std::pair<std::string,std::function<void()>>> tests = {
        {"zero-step edge texels retain both history channels",zeroStepEdges},
        {"paused render frames do not drain dynamic-wave energy",repeatedPause},
        {"zero-step camera reprojection follows the existing source coordinates",cameraReprojection},
        {"zero-step dry-land clearing is preserved",dryLand},
        {"zero-step invalid cascade clearing is preserved",invalidCascade},
        {"zero-step history still rejects nonfinite values",invalidHistory},
        {"positive-step uniform edge damping is unchanged",positiveUniform},
        {"positive-step wave propagation is unchanged",positiveLaplacian},
        {"positive-step shallow attenuation is unchanged",positiveShallows},
    };
    int failures=0;
    for (auto& test : tests)
    {
        reset();
        try { test.second(); std::cout << "PASS: " << test.first << '\n'; }
        catch (const std::exception& error) { failures++; std::cout << "FAIL: " << test.first << ": " << error.what() << '\n'; }
    }
    std::cout << "Dynamic-wave boundaries: " << tests.size()-failures << '/' << tests.size() << " passed; " << failures << " failed.\n";
    std::cout << "Actual kernel arithmetic with CPU type/sampling adapters; no Unity/GPU/half-precision validation.\n";
    return failures ? 1 : 0;
}
