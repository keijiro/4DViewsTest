using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using unity4dv;
using IntPtr = System.IntPtr;

namespace Fdvfx
{
    [ExecuteInEditMode]
    public sealed class VolumeRenderer : MonoBehaviour, ITimeControl, IPropertyPreview
    {
        #region Editable attributes

        [Space]
        [SerializeField] string _fileName = null;
        [SerializeField] float _time = 0;
        [Space]
        [SerializeField] bool _drawSurface = false;
        [SerializeField] Material _surfaceMaterial = null;
        [Space]
        [SerializeField] RenderTexture _positionMap = null;
        [SerializeField] RenderTexture _normalMap = null;
        [SerializeField] RenderTexture _uvMap = null;
        [SerializeField] RenderTexture _colorMap = null;

        #endregion

        #region Hidden attribute

        [SerializeField, HideInInspector] Shader _bakeShader = null;

        #endregion

        #region Internal-use objects

        // 4DViews data source and controller variables
        DataSource4DS _dataSource;
        int _totalFrames;
        float _frameRate;
        int _lastFrame = -1;

        // Buffers used to receive data from the 4DViews data source
        (NativeArray<Vector3> vertex,
         NativeArray<Vector3> normal,
         NativeArray<Vector2> uv,
         NativeArray<int> index,
         NativeArray<byte> texture) _sourceBuffer;

        // Objects for surface rendering
        Mesh _mesh;
        Texture2D _texture;
        MaterialPropertyBlock _overrides;

        // Buffers used to bake the point cloud
        (ComputeBuffer vertex,
         ComputeBuffer normal,
         ComputeBuffer uv,
         ComputeBuffer index) _bakeBuffer;

        // Objects for point cloud baking
        RenderBuffer[] _mrt = new RenderBuffer[3];
        Material _bakeMaterial;

        #endregion

        #region Internal-use utility function

        static void DestroySafely<T>(ref T obj) where T : Object
        {
            if (obj != null)
            {
                if (Application.isPlaying)
                    Destroy(obj);
                else
                    DestroyImmediate(obj);
            }
            obj = null;
        }

        #endregion

        #region ITimeControl implementation

        public void OnControlTimeStart() {}
        public void OnControlTimeStop() {}
        public void SetTime(double time) => _time = (float)time;

        #endregion

        #region IPropertyPreview implementation

        public void GatherProperties
            (PlayableDirector director, IPropertyCollector driver)
                => driver.AddFromName<VolumeRenderer>(gameObject, "_time");

        #endregion

        #region MonoBehaviour implementation

        void OnDisable()
        {
            // We have to release these buffers in OnDisable, not in OnDestroy.

            if (_sourceBuffer.vertex .IsCreated) _sourceBuffer.vertex .Dispose();
            if (_sourceBuffer.normal .IsCreated) _sourceBuffer.normal .Dispose();
            if (_sourceBuffer.uv     .IsCreated) _sourceBuffer.uv     .Dispose();
            if (_sourceBuffer.index  .IsCreated) _sourceBuffer.index  .Dispose();
            if (_sourceBuffer.texture.IsCreated) _sourceBuffer.texture.Dispose();

            _bakeBuffer.vertex?.Dispose();
            _bakeBuffer.normal?.Dispose();
            _bakeBuffer.uv    ?.Dispose();
            _bakeBuffer.index ?.Dispose();
            _bakeBuffer = (null, null, null, null);
        }

        void OnDestroy()
        {
            if (_dataSource != null)
            {
                Bridge4DS.DestroySequence(_dataSource.FDVUUID);
                _dataSource = null;
            }

            DestroySafely(ref _mesh);
            DestroySafely(ref _texture);
            DestroySafely(ref _bakeMaterial);
        }

        void Update()
        {
            // Data source lazy initialization
            if (_dataSource == null)
            {
                _dataSource = DataSource4DS.CreateDataSource
                    (0, _fileName, true, "", 0, -1, OUT_RANGE_MODE.Stop);

                if (_dataSource == null) return;

                _totalFrames = Bridge4DS.GetSequenceNbFrames(_dataSource.FDVUUID);
                _frameRate = Bridge4DS.GetSequenceFramerate(_dataSource.FDVUUID);
            }

            // Source buffer lazy initialization
            if (!_sourceBuffer.vertex.IsCreated)
            {
                var vcount = _dataSource.MaxVertices;
                var icount = _dataSource.MaxTriangles * 3;

                var texsize = _dataSource.TextureSize;
                texsize = texsize * texsize / 2; 

                _sourceBuffer = (
                    vertex  : new NativeArray<Vector3>( vcount, Allocator.Persistent),
                    normal  : new NativeArray<Vector3>( vcount, Allocator.Persistent),
                    uv      : new NativeArray<Vector2>( vcount, Allocator.Persistent),
                    index   : new NativeArray<    int>( icount, Allocator.Persistent),
                    texture : new NativeArray<   byte>(texsize, Allocator.Persistent)
                );
            }

            // Mesh objects lazy initialization
            if (_mesh == null)
                _mesh = new Mesh() { hideFlags = HideFlags.DontSave };

            if (_texture == null)
                _texture = new Texture2D(
                    _dataSource.TextureSize,
                    _dataSource.TextureSize,
                    _dataSource.TextureFormat,
                    false, true
                ){
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave
                };

            if (_overrides == null)
                _overrides = new MaterialPropertyBlock();

            // Point cloud bake buffer lazy initialization
            if (_bakeBuffer.vertex == null)
            {
                var vcount = _dataSource.MaxVertices;
                var tcount = _dataSource.MaxTriangles;

                _bakeBuffer = (
                    vertex : new ComputeBuffer(vcount * 3, sizeof(float)),
                    normal : new ComputeBuffer(vcount * 3, sizeof(float)),
                    uv     : new ComputeBuffer(vcount * 2, sizeof(float)),
                    index  : new ComputeBuffer(tcount * 3, sizeof(  int))
                );
            }

            // Point cloud bake shader (material) initialization
            if (_bakeMaterial == null)
                _bakeMaterial = new Material(_bakeShader){ hideFlags = HideFlags.DontSave };

            // Try receiving a new frame from the data source.
            var frame = Mathf.Clamp((int)(_time * _frameRate), 0, _totalFrames - 1);
            if (frame != _lastFrame) ReceiveNewFrame(frame);

            // Draw the surface mesh.
            if (_drawSurface && _surfaceMaterial != null)
            {
                _overrides.SetTexture("_MainTex", _texture);

                Graphics.DrawMesh(
                    _mesh, transform.localToWorldMatrix,
                    _surfaceMaterial, gameObject.layer,
                    null, 0, _overrides
                );
            }
        }

        #endregion

        #region Data transfer method

        unsafe void ReceiveNewFrame(int frame)
        {
            // Receive the mesh from the 4DViews data source.
            Bridge4DS.GotoFrame(_dataSource.FDVUUID, frame);

            int vertexCount = 0, indexCount = 0;

            var modelID = Bridge4DS.UpdateModel(
                _dataSource.FDVUUID,
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_sourceBuffer.vertex),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_sourceBuffer.uv),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_sourceBuffer.index),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_sourceBuffer.texture),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_sourceBuffer.normal),
                _lastFrame, ref vertexCount, ref indexCount
            );

            // Mesh update
            _mesh.SetVertices(_sourceBuffer.vertex);
            _mesh.SetNormals(_sourceBuffer.normal);
            _mesh.SetUVs(0, _sourceBuffer.uv);
            _mesh.SetIndices(_sourceBuffer.index, MeshTopology.Triangles, 0);

            // Texture update
            _texture.LoadRawTextureData(_sourceBuffer.texture);
            _texture.Apply();

            if (_colorMap != null) Graphics.Blit(_texture, _colorMap);

            // Bake the mesh into the point cloud attribute maps
            _bakeBuffer.vertex.SetData(_sourceBuffer.vertex);
            _bakeMaterial.SetBuffer("_VertexArray", _bakeBuffer.vertex);

            _bakeBuffer.normal.SetData(_sourceBuffer.normal);
            _bakeMaterial.SetBuffer("_NormalArray", _bakeBuffer.normal);

            _bakeBuffer.uv.SetData(_sourceBuffer.uv);
            _bakeMaterial.SetBuffer("_UVArray", _bakeBuffer.uv);

            _bakeMaterial.SetInt("_VertexCount", vertexCount);
            _bakeMaterial.SetVector("_TextureSize",
                new Vector2(_positionMap.width, _positionMap.height));

            _mrt[0] = _positionMap.colorBuffer;
            _mrt[1] = _normalMap.colorBuffer;
            _mrt[2] = _uvMap.colorBuffer;
            Graphics.SetRenderTarget(_mrt, _positionMap.depthBuffer);

            Graphics.Blit(null, _bakeMaterial, 0);

            // Frame number update
            _lastFrame = frame;
        }

        #endregion
    }
}
