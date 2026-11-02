using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Examples.Lib.render.skinning{
    [Guid("b5cecf74-96ae-4844-a64c-b0a1e9421a84")]
    internal sealed class SkinMeshExample : Instance<SkinMeshExample>
    {
        [Output(Guid = "f310d7bc-f99d-43c2-8fe8-656a65b3c0fd")]
        public readonly Slot<Texture2D> ColorBuffer = new Slot<Texture2D>();


    }
}

