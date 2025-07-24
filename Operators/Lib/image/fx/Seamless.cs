namespace Lib.image.fx;

[Guid("26507fdd-c918-4196-aec5-1a29c7f85157")]
internal sealed class Seamless : Instance<Seamless>
{
    [Output(Guid = "9f97a957-88d6-4a98-b655-dab9e116647c")]
    public readonly Slot<Texture2D> TextureOutput = new();


    [Input(Guid = "11a795c4-800d-46dd-987c-448c3b7168c7")]
    public readonly InputSlot<Texture2D> Image = new();

        [Input(Guid = "58906b39-d294-456e-815e-fc4162ad6884")]
        public readonly InputSlot<float> EdgeFallOff = new InputSlot<float>();

        [Input(Guid = "292dbf72-1bb1-4ef2-a322-97a60e345df7")]
        public readonly InputSlot<int> TillingMode = new InputSlot<int>();
}