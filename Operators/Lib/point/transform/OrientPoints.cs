namespace Lib.point.transform;

[Guid("acc71a14-daad-4b36-b0bc-cf0a796cc5d9")]
internal sealed class OrientPoints : Instance<OrientPoints>
{

    [Output(Guid = "23a08560-9764-42a1-a889-dd8839476747")]
    public readonly Slot<BufferWithViews> Output = new();

    [Input(Guid = "865ad090-0fdd-4683-ba93-b6be92b55cb3")]
    public readonly InputSlot<BufferWithViews> Points = new();

    [Input(Guid = "4fec5414-16a2-4b48-9605-1bc3e7f464b5")]
    public readonly InputSlot<float> Amount = new();

    [Input(Guid = "607fd90d-57f3-4a6a-b843-86c7170c854c")]
    public readonly InputSlot<Vector3> Center = new();

    [Input(Guid = "2aa74709-65f3-49fa-9890-f0a0f6e76bbf")]
    public readonly InputSlot<Vector3> UpVector = new();

    [Input(Guid = "4358e71b-3f33-4868-af4d-97e8e04087a6")]
    public readonly InputSlot<bool> WIsWeight = new();

    [Input(Guid = "02ae76ba-7be8-4112-a59b-55616343f1dd")]
    public readonly InputSlot<bool> Flip = new();

        [Input(Guid = "2f61aa1d-fb5e-478e-9f20-3515c9273705")]
        public readonly InputSlot<int> Mode = new InputSlot<int>();

        [Input(Guid = "8447b489-0605-45e3-b195-fbd313671b38")]
        public readonly InputSlot<float> BaseScale = new InputSlot<float>();

        [Input(Guid = "6b681140-bf8e-4751-b39b-b6b278b7a694")]
        public readonly InputSlot<int> ScaleMode = new InputSlot<int>();

        [Input(Guid = "259fe1fb-fb9b-4bbc-8c4b-babe307c385e")]
        public readonly InputSlot<float> Fov = new InputSlot<float>();
}