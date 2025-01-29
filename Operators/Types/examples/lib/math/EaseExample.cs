using SharpDX.Direct3D11;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;

namespace T3.Operators.Types.Id_809d1560_349b_4b34_ac37_2770c9f76d98
{
    public class EaseExample : Instance<EaseExample>
    {
        [Output(Guid = "e30d59d4-b71a-40cd-b859-66b36c006012")]
        public readonly Slot<Texture2D> ColorBuffer = new Slot<Texture2D>();


    }
}

