// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

// Potential improvements
// - Half return values
// - Half minGridSize

using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Crest
{
    /// <summary>
    /// Provides heights and other physical data about the water surface. Works by uploading query positions to GPU and computing
    /// the data and then transferring back the results asynchronously. An exception to this is water surface velocities - these can
    /// not be computed on the GPU and are instead computed on the CPU by retaining last frames' query results and computing finite diffs.
    /// </summary>
    public abstract class QueryBase
    {
        protected int _kernelHandle;

        protected abstract string QueryShaderName { get; }
        protected abstract string QueryKernelName { get; }

        // 4 was enough for a long time, but Linux setups seems to demand 7
        const int s_maxRequests = 7;
        const int s_maxGuids = 1024;

        protected virtual ComputeShader ShaderProcessQueries => _shaderProcessQueries;
        ComputeShader _shaderProcessQueries;
        PropertyWrapperComputeStandalone _wrapper;

        System.Action<AsyncGPUReadbackRequest> _dataArrivedAction;

        // Must match value in compute shader
        const int s_computeGroupSize = 64;
        public static bool s_useComputeCollQueries = true;

        readonly int sp_queryPositions_minGridSizes = Shader.PropertyToID("_QueryPositions_MinGridSizes");

        const float s_finiteDiffDx = 0.1f;

        ComputeBuffer _computeBufQueries;
        ComputeBuffer _computeBufResults;

        public const int MAX_QUERY_COUNT_DEFAULT = 4096;

        int _maxQueryCount = MAX_QUERY_COUNT_DEFAULT;
        Vector3[] _queryPosXZ_minGridSize;
        Vector3[] _queryPosScratch;
        bool _disposed;

        struct QuerySegment
        {
            public int _start, _end, _lastUsedFrame;
            // The readback layout belongs to the submitted batch, not to the caller's current outputs.
            public int _pointCount, _normalCount;
        }

        /// <summary>
        /// Holds information about all query points. Maps from unique hash code to position in point array.
        /// </summary>
        class SegmentRegistrar
        {
            public readonly Dictionary<int, QuerySegment> _segments = new Dictionary<int, QuerySegment>();
            public int _numQueries = 0;
            public bool _inUse;
        }

        /// <summary>
        /// A registrar is owned by the current batch, an in-flight request, or one of the two retained results.
        /// Completion can be out of order, so release the exact registrar rather than advancing a ring cursor.
        /// </summary>
        class SegmentRegistrarPool
        {
            // Requests in flight plus 2 held values, plus one current
            readonly static int s_poolSize = s_maxRequests + 2 + 1;

            readonly SegmentRegistrar[] _segments = new SegmentRegistrar[s_poolSize];

            public SegmentRegistrar Current { get; set; }

            public SegmentRegistrarPool()
            {
                for (int i = 0; i < _segments.Length; i++)
                {
                    _segments[i] = new SegmentRegistrar();
                }

                Current = Acquire();
            }

            public SegmentRegistrar Acquire()
            {
                for (int i = 0; i < _segments.Length; i++)
                {
                    if (!_segments[i]._inUse)
                    {
                        _segments[i]._inUse = true;
                        return _segments[i];
                    }
                }

                return null;
            }

            public void Release(SegmentRegistrar registrar)
            {
                if (registrar != null)
                {
                    Debug.Assert(registrar._inUse);
                    registrar._segments.Clear();
                    registrar._numQueries = 0;
                    registrar._inUse = false;
                }
            }
        }

        readonly SegmentRegistrarPool _segmentRegistrarPool = new SegmentRegistrarPool();

        NativeArray<Vector3> _queryResults;
        float _queryResultsTime = -1f;
        SegmentRegistrar _resultRegistrar;
        Dictionary<int, QuerySegment> _resultSegments => _resultRegistrar?._segments;

        NativeArray<Vector3> _queryResultsLast;
        float _queryResultsTimeLast = -1f;
        SegmentRegistrar _resultRegistrarLast;
        Dictionary<int, QuerySegment> _resultSegmentsLast => _resultRegistrarLast?._segments;
        long _nextRequestSequence;
        long _queryResultsSequence;

        struct ReadbackRequest
        {
            public AsyncGPUReadbackRequest _request;
            public float _dataTimestamp;
            public SegmentRegistrar _registrar;
            public long _sequence;
        }

        readonly List<ReadbackRequest> _requests = new List<ReadbackRequest>(s_maxRequests);

        public enum QueryStatus
        {
            OK = 0,
            RetrieveFailed = 1,
            PostFailed = 2,
            NotEnoughDataForVels = 4,
            VelocityDataInvalidated = 8,
            InvalidDtForVelocity = 16,
        }

        public QueryBase()
        {
            _dataArrivedAction = new System.Action<AsyncGPUReadbackRequest>(DataArrived);

            _maxQueryCount = OceanRenderer.Instance._lodDataAnimWaves.Settings.MaxQueryCount;
            _queryPosXZ_minGridSize = new Vector3[_maxQueryCount];
            _queryPosScratch = new Vector3[_maxQueryCount];

            _computeBufQueries = new ComputeBuffer(_maxQueryCount, 12, ComputeBufferType.Default);
            _computeBufResults = new ComputeBuffer(_maxQueryCount, 12, ComputeBufferType.Default);

            _queryResults = new NativeArray<Vector3>(_maxQueryCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _queryResultsLast = new NativeArray<Vector3>(_maxQueryCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            _shaderProcessQueries = ComputeShaderHelpers.LoadShader(QueryShaderName);
            if (_shaderProcessQueries == null)
            {
                Debug.LogError($"Crest: Could not load Query compute shader {QueryShaderName}");
                CleanUp();
                return;
            }
            _kernelHandle = _shaderProcessQueries.FindKernel(QueryKernelName);
            _wrapper = new PropertyWrapperComputeStandalone(_shaderProcessQueries, _kernelHandle);

            Application.quitting += OnQuitting;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.quitting += OnQuitting;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnQuitting;
#endif
        }

        protected abstract void BindInputsAndOutputs(PropertyWrapperComputeStandalone wrapper, ComputeBuffer resultsBuffer);

        /// <summary>
        /// Takes a unique request ID and some world space XZ positions, and computes the displacement vector that lands at this position,
        /// to a good approximation. The world space height of the water at that position is then SeaLevel + displacement.y.
        /// </summary>
        protected bool UpdateQueryPoints(int i_ownerHash, float i_minSpatialLength, Vector3[] queryPoints, Vector3[] queryNormals)
        {
            if (_disposed)
            {
                return false;
            }

            // We'll send in 3 points to get normals
            var countPts = (queryPoints != null ? queryPoints.Length : 0);
            var countNorms = (queryNormals != null ? queryNormals.Length : 0);
            var countTotal = (long)countPts + (long)countNorms * 3;

            if (countTotal == 0)
            {
                // No query data
                return false;
            }

            var registrar = _segmentRegistrarPool.Current;
            var hasSegment = registrar._segments.TryGetValue(i_ownerHash, out var segment);
            var reuseSegment = hasSegment && segment._end - segment._start + 1 == countTotal;

            // Check all points, including the three samples per normal, before changing the registration.
            // Updating an existing range needs no additional space, even when the buffer is full.
            if (!reuseSegment && countTotal > _maxQueryCount - registrar._numQueries)
            {
                Debug.LogError($"Crest: Max query count ({_maxQueryCount}) exceeded, increase the max query count in the Animated Waves Settings to support a higher number of queries.");
                return false;
            }

            if (!reuseSegment)
            {
                if (!hasSegment && registrar._segments.Count >= s_maxGuids)
                {
                    Debug.LogError("Crest: Too many guids registered with CollProviderCompute. Increase s_maxGuids.");
                    return false;
                }

                segment._start = registrar._numQueries;
                segment._end = segment._start + (int)countTotal - 1;
                registrar._numQueries += (int)countTotal;
            }

            segment._lastUsedFrame = Time.frameCount;
            segment._pointCount = countPts;
            segment._normalCount = countNorms;
            registrar._segments[i_ownerHash] = segment;

            // The smallest wavelengths should repeat no more than twice across the smaller spatial length. Unless we're
            // in the last LOD - then this is the best we can do.
            float minWavelength = i_minSpatialLength / 2f;
            float samplesPerWave = 2f;
            float minGridSize = minWavelength / samplesPerWave;

            for (int pointi = 0; pointi < countPts; pointi++)
            {
                _queryPosXZ_minGridSize[pointi + segment._start].x = queryPoints[pointi].x;
                _queryPosXZ_minGridSize[pointi + segment._start].y = queryPoints[pointi].z;
                _queryPosXZ_minGridSize[pointi + segment._start].z = minGridSize;
            }

            // To compute each normal, post 3 query points
            for (int normi = 0; normi < countNorms; normi++)
            {
                var arrIdx = segment._start + countPts + 3 * normi;

                _queryPosXZ_minGridSize[arrIdx + 0].x = queryNormals[normi].x;
                _queryPosXZ_minGridSize[arrIdx + 0].y = queryNormals[normi].z;
                _queryPosXZ_minGridSize[arrIdx + 0].z = minGridSize;

                _queryPosXZ_minGridSize[arrIdx + 1].x = queryNormals[normi].x + s_finiteDiffDx;
                _queryPosXZ_minGridSize[arrIdx + 1].y = queryNormals[normi].z;
                _queryPosXZ_minGridSize[arrIdx + 1].z = minGridSize;

                _queryPosXZ_minGridSize[arrIdx + 2].x = queryNormals[normi].x;
                _queryPosXZ_minGridSize[arrIdx + 2].y = queryNormals[normi].z + s_finiteDiffDx;
                _queryPosXZ_minGridSize[arrIdx + 2].z = minGridSize;
            }

            return true;
        }

        /// <summary>
        /// Signal that we're no longer servicing queries. Note this leaves an air bubble in the query buffer.
        /// </summary>
        public void RemoveQueryPoints(int guid)
        {
            if (!_disposed)
            {
                _segmentRegistrarPool.Current._segments.Remove(guid);
            }
        }

        /// <summary>
        /// Remove air bubbles from the query array. Currently this lazily just nukes all the registered
        /// query IDs so they'll be recreated next time (generating garbage).
        /// </summary>
        public void CompactQueryStorage()
        {
            if (!_disposed)
            {
                _segmentRegistrarPool.Current._segments.Clear();
                _segmentRegistrarPool.Current._numQueries = 0;
            }
        }

        /// <summary>
        /// Copy out displacements, heights, normals. Pass null if info is not required.
        /// </summary>
        protected bool RetrieveResults(int guid, Vector3[] displacements, float[] heights, Vector3[] normals)
        {
            if (_resultSegments == null)
            {
                return false;
            }

            // Check if there are results that came back for this guid
            if (!_resultSegments.TryGetValue(guid, out var segment))
            {
                // Guid not found - no result
                return false;
            }

            var countPoints = 0;
            if (displacements != null) countPoints = displacements.Length;
            if (heights != null) countPoints = heights.Length;
            var countNorms = (normals != null ? normals.Length : 0);

            // A caller may resize its query before the new readback arrives. Never read another owner's range
            // or partially overwrite outputs on failure. Partial/optional outputs remain supported.
            if ((displacements != null && heights != null && displacements.Length != heights.Length)
                || countPoints > segment._pointCount || countNorms > segment._normalCount
                || !HasResultRange(segment, _queryResults.Length, (long)segment._pointCount + (long)segment._normalCount * 3))
            {
                return false;
            }

            if (countPoints > 0)
            {
                // Retrieve Results
                if (displacements != null) _queryResults.Slice(segment._start, countPoints).CopyTo(displacements);

                // Retrieve Result heights
                if (heights != null)
                {
                    var seaLevel = OceanRenderer.Instance.SeaLevel;
                    for (int i = 0; i < countPoints; i++)
                    {
                        heights[i] = seaLevel + _queryResults[i + segment._start].y;
                    }
                }
            }

            if (countNorms > 0)
            {
                int firstNorm = segment._start + segment._pointCount;

                var dx = -Vector3.right * s_finiteDiffDx;
                var dz = -Vector3.forward * s_finiteDiffDx;
                for (int i = 0; i < countNorms; i++)
                {
                    var p = _queryResults[firstNorm + 3 * i + 0];
                    var px = dx + _queryResults[firstNorm + 3 * i + 1];
                    var pz = dz + _queryResults[firstNorm + 3 * i + 2];

                    normals[i] = Vector3.Cross(p - px, p - pz).normalized;
                    normals[i].y *= -1f;
                }
            }

            return true;
        }

        static bool HasResultRange(QuerySegment segment, int resultCount, long requestedCount)
        {
            return segment._start >= 0 && segment._end >= segment._start && segment._end < resultCount
                && requestedCount <= (long)segment._end - segment._start + 1;
        }

        /// <summary>
        /// Compute time derivative of the displacements by calculating difference from last query. More complicated than it would seem - results
        /// may not be available in one or both of the results, or the query locations in the array may change.
        /// </summary>
        protected int CalculateVelocities(int i_ownerHash, Vector3[] results)
        {
            // Need at least 2 returned results to do finite difference
            if (_queryResultsTime < 0f || _queryResultsTimeLast < 0f)
            {
                return 1;
            }

            if (!_resultSegments.TryGetValue(i_ownerHash, out var segment))
            {
                return (int)QueryStatus.RetrieveFailed;
            }

            if (!_resultSegmentsLast.TryGetValue(i_ownerHash, out var segmentLast))
            {
                return (int)QueryStatus.NotEnoughDataForVels;
            }

            if (segment._pointCount != segmentLast._pointCount || results.Length > segment._pointCount
                || !HasResultRange(segment, _queryResults.Length, results.Length)
                || !HasResultRange(segmentLast, _queryResultsLast.Length, results.Length))
            {
                // Number of queries changed, or the caller is asking beyond a retained range.
                return (int)QueryStatus.VelocityDataInvalidated;
            }

            var dt = _queryResultsTime - _queryResultsTimeLast;
            if (dt < 0.0001f)
            {
                return (int)QueryStatus.InvalidDtForVelocity;
            }

            var count = results.Length;
            for (var i = 0; i < count; i++)
            {
                results[i] = (_queryResults[i + segment._start] - _queryResultsLast[i + segmentLast._start]) / dt;
            }

            return 0;
        }

        public void UpdateQueries()
        {
            ProcessCompletedRequests();

            // Keep every in-flight request accounted for. When readbacks stall, retain the latest query positions
            // and the last valid results instead of dispatching more GPU work or reusing an active registrar.
            if (_disposed || _requests.Count >= s_maxRequests || _segmentRegistrarPool.Current._numQueries == 0)
            {
                return;
            }

            // Reserve space before dispatch. With the request limit and two retained results, a slot must be free.
            var nextRegistrar = _segmentRegistrarPool.Acquire();
            if (nextRegistrar == null)
            {
                Debug.LogError("Crest: Query registrar pool has no free slot.");
                return;
            }

            var submitted = false;
            try
            {
                PrepareNextBatch(nextRegistrar);

                // Dispatch still uses the original registrar and coordinates. Only switch the writable batch after
                // its readback is tracked, so exceptions cannot expose a submitted mapping to later query writes.
                ExecuteQueries();

                var request = new ReadbackRequest
                {
                    _dataTimestamp = Time.time,
                    _registrar = _segmentRegistrarPool.Current,
                    _sequence = ++_nextRequestSequence,
                    _request = AsyncGPUReadback.Request(_computeBufResults, _dataArrivedAction),
                };
                _requests.Add(request);

                _segmentRegistrarPool.Current = nextRegistrar;
                Helpers.Swap(ref _queryPosXZ_minGridSize, ref _queryPosScratch);
                submitted = true;
            }
            finally
            {
                if (!submitted)
                {
                    _segmentRegistrarPool.Release(nextRegistrar);
                }
            }

            // A request may already have failed, or its callback may have run before Request returned.
            ProcessCompletedRequests();
        }

        void PrepareNextBatch(SegmentRegistrar nextRegistrar)
        {
            // FixedUpdate may not run on every rendered frame. Keep recent queries, and move their positions with
            // their ranges. A separate array avoids overwriting unread ranges when dictionary order changes.
            foreach (var segment in _segmentRegistrarPool.Current._segments)
            {
                if (Time.frameCount - segment.Value._lastUsedFrame < 10)
                {
                    var newSegment = segment.Value;
                    var count = newSegment._end - newSegment._start + 1;
                    newSegment._start = nextRegistrar._numQueries;
                    newSegment._end = newSegment._start + count - 1;
                    System.Array.Copy(_queryPosXZ_minGridSize, segment.Value._start, _queryPosScratch, newSegment._start, count);
                    nextRegistrar._segments.Add(segment.Key, newSegment);
                    nextRegistrar._numQueries += count;
                }
            }
        }

        void ExecuteQueries()
        {
            _computeBufQueries.SetData(_queryPosXZ_minGridSize, 0, 0, _segmentRegistrarPool.Current._numQueries);
            _shaderProcessQueries.SetBuffer(_kernelHandle, sp_queryPositions_minGridSizes, _computeBufQueries);
            BindInputsAndOutputs(_wrapper, _computeBufResults);

            var numGroups = (_segmentRegistrarPool.Current._numQueries + s_computeGroupSize - 1) / s_computeGroupSize;
            _shaderProcessQueries.Dispatch(_kernelHandle, numGroups, 1, 1);
        }

        /// <summary>
        /// Called when a compute buffer has been read back from the GPU to the CPU.
        /// </summary>
        void DataArrived(AsyncGPUReadbackRequest req)
        {
            ProcessCompletedRequests();
        }

        void ProcessCompletedRequests()
        {
            var newestIndex = -1;
            var newestSequence = _queryResultsSequence;
            if (!_disposed)
            {
                for (int i = 0; i < _requests.Count; i++)
                {
                    var request = _requests[i];
                    if (request._request.done && !request._request.hasError && request._sequence > newestSequence)
                    {
                        newestIndex = i;
                        newestSequence = request._sequence;
                    }
                }
            }

            if (newestIndex >= 0)
            {
                var request = _requests[newestIndex];
                // Unity owns request data for only one frame. Copy it into our persistent storage immediately.
                var data = request._request.GetData<Vector3>();
                data.CopyTo(_queryResultsLast);

                Helpers.Swap(ref _queryResults, ref _queryResultsLast);
                _segmentRegistrarPool.Release(_resultRegistrarLast);
                _queryResultsTimeLast = _queryResultsTime;
                _resultRegistrarLast = _resultRegistrar;
                _queryResultsTime = request._dataTimestamp;
                _resultRegistrar = request._registrar;
                _queryResultsSequence = newestSequence;
            }

            for (int i = _requests.Count - 1; i >= 0; --i)
            {
                var request = _requests[i];
                if (request._request.done || request._request.hasError)
                {
                    // The selected result takes ownership of its registrar. Errors and stale completed results
                    // release only their own slots; older requests which are still pending remain counted.
                    if (i != newestIndex)
                    {
                        _segmentRegistrarPool.Release(request._registrar);
                    }
                    _requests.RemoveAt(i);
                }
            }

            if (_disposed && _requests.Count == 0)
            {
                DisposeBuffers();
            }
        }

        public void CleanUp()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _queryResults.Dispose();
            _queryResultsLast.Dispose();
            _queryResultsTime = _queryResultsTimeLast = -1f;

            _segmentRegistrarPool.Release(_segmentRegistrarPool.Current);
            _segmentRegistrarPool.Current = null;
            _segmentRegistrarPool.Release(_resultRegistrar);
            _segmentRegistrarPool.Release(_resultRegistrarLast);
            _resultRegistrar = _resultRegistrarLast = null;

            // Late callbacks must not access the disposed CPU results. Keep the GPU source buffers alive until
            // this instance's outstanding requests finish. Normal cleanup does not block the player loop.
            ProcessCompletedRequests();
        }

        void OnQuitting()
        {
            CleanUp();

            // There will be no later player-loop callback after quitting or an editor domain reload.
            // Wait only for this provider's requests; callbacks can remove entries from the live list.
            var requests = _requests.ToArray();
            foreach (var request in requests)
            {
                if (!request._request.done && !request._request.hasError)
                {
                    request._request.WaitForCompletion();
                }
            }
            ProcessCompletedRequests();
        }

        void DisposeBuffers()
        {
            _computeBufQueries?.Dispose();
            _computeBufResults?.Dispose();
            _computeBufQueries = _computeBufResults = null;

            // Keep shutdown handlers registered while cleanup still has pending readbacks.
            Application.quitting -= OnQuitting;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.quitting -= OnQuitting;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnQuitting;
#endif
        }

        public int Query(int i_ownerHash, float i_minSpatialLength, Vector3[] i_queryPoints, Vector3[] o_resultDisps, Vector3[] o_resultNorms, Vector3[] o_resultVels)
        {
            var result = (int)QueryStatus.OK;

            if (!UpdateQueryPoints(i_ownerHash, i_minSpatialLength, i_queryPoints, o_resultNorms != null ? i_queryPoints : null))
            {
                result |= (int)QueryStatus.PostFailed;
            }

            if (!RetrieveResults(i_ownerHash, o_resultDisps, null, o_resultNorms))
            {
                result |= (int)QueryStatus.RetrieveFailed;
            }

            if (o_resultVels != null)
            {
                result |= CalculateVelocities(i_ownerHash, o_resultVels);
            }

            return result;
        }

        public bool RetrieveSucceeded(int queryStatus)
        {
            return (queryStatus & (int)QueryStatus.RetrieveFailed) == 0;
        }

        public int ResultGuidCount => _resultSegments != null ? _resultSegments.Count : 0;

        public int RequestCount => _requests != null ? _requests.Count : 0;
    }
}
