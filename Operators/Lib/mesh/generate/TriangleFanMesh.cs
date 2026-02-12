namespace Lib.mesh.generate;

[Guid("19c70274-8560-43d1-9d7d-2d0ffa4195a7")]
internal sealed class TriangleFanMesh :Instance<TriangleFanMesh>{

    [Output(Guid = "b55f0cf1-2c61-425c-a549-3ef65281d859")]
    public readonly Slot<MeshBuffers> Output2 = new();

        [Input(Guid = "42ca8a2c-0cb6-4841-b222-53182eecc87d")]
        public readonly InputSlot<T3.Core.DataTypes.BufferWithViews> Points = new InputSlot<T3.Core.DataTypes.BufferWithViews>();

        [Input(Guid = "7a8503e7-8974-4dfd-89e2-e4a4b4c235ff")]
        public readonly InputSlot<int> UvMode = new InputSlot<int>();

        [Input(Guid = "99210ce1-372c-4d7c-aecc-5fe81f6b6d19")]
        public readonly InputSlot<bool> CloseFan = new InputSlot<bool>();


    private enum SampleModes
    {
        StartEnd,
        StartLength,
    }
    
    private enum FModes
    {
        None,
        RailPoint_F1,
        RailPoint_F2,
    }
}