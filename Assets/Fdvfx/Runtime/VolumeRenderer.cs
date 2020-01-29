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
        [SerializeField] string _fileName = null;
        [SerializeField] Material _material = null;
        [SerializeField] float _time = 0;
        [SerializeField] RenderTexture _positionMap = null;
        [SerializeField] RenderTexture _uvMap = null;
        [SerializeField] RenderTexture _colorMap = null;

        [SerializeField, HideInInspector] Shader _bakeShader = null;

        DataSource4DS _dataSource;
        int _totalFrames;
        float _frameRate;
        int _lastFrame = -1;

        Mesh _mesh;
        Texture2D _texture;
        MaterialPropertyBlock _props;

        RenderBuffer[] _mrt = new RenderBuffer[2];

        Material _bakeMaterial;

        (
            NativeArray<Vector3> vertex,
            NativeArray<Vector3> normal,
            NativeArray<Vector2> uv,
            NativeArray<int> index,
            NativeArray<byte> texture
        )
        _buffer;

        (
            ComputeBuffer vertex,
            ComputeBuffer normal,
            ComputeBuffer uv,
            ComputeBuffer index
        )
        _bakeBuffer;

        void OnDisable()
        {
            if (_buffer.vertex .IsCreated) _buffer.vertex .Dispose();
            if (_buffer.normal .IsCreated) _buffer.normal .Dispose();
            if (_buffer.uv     .IsCreated) _buffer.uv     .Dispose();
            if (_buffer.index  .IsCreated) _buffer.index  .Dispose();
            if (_buffer.texture.IsCreated) _buffer.texture.Dispose();

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

            if (_mesh != null)
            {
                if (Application.isPlaying)
                    Destroy(_mesh);
                else
                    DestroyImmediate(_mesh);
                _mesh = null;
            }

            if (_texture != null)
            {
                if (Application.isPlaying)
                    Destroy(_texture);
                else
                    DestroyImmediate(_texture);
                _texture = null;
            }

            if (_bakeMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(_bakeMaterial);
                else
                    DestroyImmediate(_bakeMaterial);
                _bakeMaterial = null;
            }
        }

        unsafe void Update()
        {
            if (_dataSource == null)
            {
                _dataSource = DataSource4DS.CreateDataSource
                    (0, _fileName, true, "", 0, -1, OUT_RANGE_MODE.Stop);

                if (_dataSource == null) return;

                _totalFrames = Bridge4DS.GetSequenceNbFrames(_dataSource.FDVUUID);
                _frameRate = Bridge4DS.GetSequenceFramerate(_dataSource.FDVUUID);

                Bridge4DS.SetSpeed(_dataSource.FDVUUID, 0);
                Bridge4DS.Play(_dataSource.FDVUUID, true);
            }

            if (!_buffer.vertex.IsCreated)
            {
                var vcount = _dataSource.MaxVertices;
                var icount = _dataSource.MaxTriangles * 3;

                var texsize = _dataSource.TextureSize;
                texsize = texsize * texsize / 2; 

                _buffer.vertex  = new NativeArray<Vector3>( vcount, Allocator.Persistent);
                _buffer.normal  = new NativeArray<Vector3>( vcount, Allocator.Persistent);
                _buffer.uv      = new NativeArray<Vector2>( vcount, Allocator.Persistent);
                _buffer.index   = new NativeArray<    int>( icount, Allocator.Persistent);
                _buffer.texture = new NativeArray<   byte>(texsize, Allocator.Persistent);
            }

            if (_bakeBuffer.vertex == null)
            {
                var vcount = _dataSource.MaxVertices;
                var tcount = _dataSource.MaxTriangles;

                _bakeBuffer.vertex  = new ComputeBuffer(vcount * 3, sizeof(float));
                _bakeBuffer.normal  = new ComputeBuffer(vcount * 3, sizeof(float));
                _bakeBuffer.uv      = new ComputeBuffer(vcount * 2, sizeof(float));
                _bakeBuffer.index   = new ComputeBuffer(tcount * 3, sizeof(int));
            }

            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.hideFlags = HideFlags.DontSave;
            }

            if (_texture == null)
            {
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
            }

            if (_props == null) _props = new MaterialPropertyBlock();

            if (_bakeMaterial == null)
                _bakeMaterial =
                    new Material(_bakeShader){ hideFlags = HideFlags.DontSave };

            if (_dataSource != null)
            {
                var frame = Mathf.Clamp((int)(_time * _frameRate), 0, _totalFrames - 1);

                if (frame != _lastFrame)
                {
                    Bridge4DS.GotoFrame(_dataSource.FDVUUID, frame);

                    int vertexCount = 0, indexCount = 0;

                    var modelID = Bridge4DS.UpdateModel(
                        _dataSource.FDVUUID,
                        (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.vertex),
                        (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.uv),
                        (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.index),
                        (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.texture),
                        (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.normal),
                        _lastFrame, ref vertexCount, ref indexCount
                    );

                    _bakeMaterial.SetInt("_VertexCount", vertexCount);

                    _mesh.SetVertices(_buffer.vertex);
                    _mesh.SetNormals(_buffer.normal);
                    _mesh.SetUVs(0, _buffer.uv);
                    _mesh.SetIndices(_buffer.index, MeshTopology.Triangles, 0);

                    _texture.LoadRawTextureData(_buffer.texture);
                    _texture.Apply();

                    _bakeBuffer.vertex.SetData(_buffer.vertex);
                    _bakeBuffer.uv.SetData(_buffer.uv);

                    _lastFrame = frame;
                }
            }

            _props.SetTexture("_MainTex", _texture);

            _bakeMaterial.SetVector("_TextureSize", new Vector2(_positionMap.width, _positionMap.height));
            _bakeMaterial.SetBuffer("_VertexArray", _bakeBuffer.vertex);
            _bakeMaterial.SetBuffer("_UVArray", _bakeBuffer.uv);

            _mrt[0] = _positionMap.colorBuffer;
            _mrt[1] = _uvMap.colorBuffer;
            Graphics.SetRenderTarget(_mrt, _positionMap.depthBuffer);
            Graphics.Blit(null, _bakeMaterial, 0);
            Graphics.Blit(_texture, _colorMap);
        }

        #region ITimeControl implementation

        bool _externalTime;

        public void OnControlTimeStart()
        {
        }

        public void OnControlTimeStop()
        {
        }

        public void SetTime(double time)
        {
            _time = (float)time;
        }

        #endregion

        #region IPropertyPreview implementation

        public void GatherProperties(PlayableDirector director, IPropertyCollector driver)
        {
            driver.AddFromName<VolumeRenderer>(gameObject, "_time");
        }

        #endregion
    }
}
