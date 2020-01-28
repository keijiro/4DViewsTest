using UnityEngine;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using unity4dv;
using IntPtr = System.IntPtr;

public sealed class GeometryRenderer4DS : MonoBehaviour
{
    [SerializeField] string _fileName = null;
    [SerializeField] Material _material = null;

    DataSource4DS _dataSource;
    int _lastModelID = -1;

    Mesh _mesh;
    Texture2D _texture;
    MaterialPropertyBlock _props;

    void OnDisable()
    {
        if (_buffer.vertex .IsCreated) _buffer.vertex .Dispose();
        if (_buffer.normal .IsCreated) _buffer.normal .Dispose();
        if (_buffer.uv     .IsCreated) _buffer.uv     .Dispose();
        if (_buffer.index  .IsCreated) _buffer.index  .Dispose();
        if (_buffer.texture.IsCreated) _buffer.texture.Dispose();
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
            Destroy(_mesh);
            _mesh = null;
        }

        if (_texture != null)
        {
            Destroy(_texture);
            _texture = null;
        }
    }

    (
        NativeArray<Vector3> vertex,
        NativeArray<Vector3> normal,
        NativeArray<Vector2> uv,
        NativeArray<int> index,
        NativeArray<byte> texture
    )
    _buffer;

    unsafe void Update()
    {
        if (_dataSource == null)
        {
            _dataSource = DataSource4DS.CreateDataSource
                (0, _fileName, true, "", 0, -1, OUT_RANGE_MODE.Stop);

            Bridge4DS.Play(_dataSource.FDVUUID, true);

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

        if (_mesh == null) _mesh = new Mesh();

        if (_texture == null)
        {
            _texture = new Texture2D(
                _dataSource.TextureSize,
                _dataSource.TextureSize,
                _dataSource.TextureFormat,
                false
            ){
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        if (_props == null) _props = new MaterialPropertyBlock();

        if (_dataSource != null)
        {
            int vertexCount = 0, indexCount = 0;

            var modelID = Bridge4DS.UpdateModel(
                _dataSource.FDVUUID,
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.vertex),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.uv),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.index),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.texture),
                (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_buffer.normal),
                _lastModelID, ref vertexCount, ref indexCount
            );

            _lastModelID = modelID;

            _mesh.SetVertices(_buffer.vertex);
            _mesh.SetNormals(_buffer.normal);
            _mesh.SetUVs(0, _buffer.uv);
            _mesh.SetIndices(_buffer.index, MeshTopology.Triangles, 0);

            _texture.LoadRawTextureData(_buffer.texture);
            _texture.Apply();
        }

        _props.SetTexture("_MainTex", _texture);

        Graphics.DrawMesh(
            _mesh, transform.localToWorldMatrix,
            _material, 0, null, 0, _props
        );
    }
}
