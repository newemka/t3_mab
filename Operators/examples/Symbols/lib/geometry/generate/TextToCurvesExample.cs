using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Examples.Lib.geometry.generate{
    [Guid("0632bbd8-f505-40fc-ab74-edf6efdc141f")]
    internal sealed class TextToCurvesExample :Instance<TextToCurvesExample>    {
        [Output(Guid = "0781e82d-e7f2-4e64-be38-f76c285d7a41")]
        public readonly Slot<Texture2D> ColorBuffer = new Slot<Texture2D>();


    }
}

