using System;
using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Interfaces;
using T3.Core.Operator.Slots;

namespace T3.Operators.Types.Id_0e8ddc37_1c33_4c57_ac87_e1f73de27d60
{
    public class TextNormals : Instance<TextNormals>
,ITransformable
    {
        public enum HorizontalAligns
        {
            Left,
            Center,
            Right,
        }
        
        public enum VerticalAligns
        {
            Top,
            Middle,
            Bottom,
        }
        
        [Output(Guid = "508fe11e-e91d-4cb0-8ad3-3a53c71a1a6d", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
        public readonly TransformCallbackSlot<Command> Output = new();

        
        public TextNormals()
        {
            Output.TransformableOp = this;
        }
        
        IInputSlot ITransformable.TranslationInput => Position;
        IInputSlot ITransformable.RotationInput => null;
        IInputSlot ITransformable.ScaleInput => null;
        public Action<Instance, EvaluationContext> TransformCallback { get; set; }

        [Input(Guid = "e467c98f-61ef-4179-a657-85e641737339")]
        public readonly InputSlot<string> InputText = new InputSlot<string>();

        [Input(Guid = "dc8dfad5-c5b1-4883-8050-cfede27a7019")]
        public readonly InputSlot<System.Numerics.Vector4> Color = new InputSlot<System.Numerics.Vector4>();

        [Input(Guid = "8ee45323-c331-4f8a-a224-2092b113112a")]
        public readonly InputSlot<System.Numerics.Vector4> Shadow = new InputSlot<System.Numerics.Vector4>();

        [Input(Guid = "ab88682c-d1fd-4f71-9470-59eab94dab5e")]
        public readonly InputSlot<System.Numerics.Vector2> Position = new InputSlot<System.Numerics.Vector2>();

        [Input(Guid = "8d7c8b88-b8bb-44c9-add0-909f0afa23f7")]
        public readonly InputSlot<string> FontPath = new InputSlot<string>();

        [Input(Guid = "cbd17888-6ccd-4399-9c56-6053ee84a154")]
        public readonly InputSlot<float> Size = new InputSlot<float>();

        [Input(Guid = "290dd297-1ab6-4dab-9dfc-ed291999b0da")]
        public readonly InputSlot<float> Spacing = new InputSlot<float>();

        [Input(Guid = "68d8aacd-7d89-4320-aea4-f9c612e22e87")]
        public readonly InputSlot<float> LineHeight = new InputSlot<float>();

        [Input(Guid = "44922d5a-f3c5-4abf-916d-7416e77680d1", MappedType =  typeof(VerticalAligns))]
        public readonly InputSlot<int> VerticalAlign = new InputSlot<int>();

        [Input(Guid = "90d89a49-38f7-46ff-9d63-39461d8a8f49", MappedType = typeof(HorizontalAligns))]
        public readonly InputSlot<int> HorizontalAlign = new InputSlot<int>();

        [Input(Guid = "27fb09c8-2cb1-4e72-9e12-2f6c0887067d")]
        public readonly InputSlot<SharpDX.Direct3D11.CullMode> CullMode = new InputSlot<SharpDX.Direct3D11.CullMode>();

        [Input(Guid = "c3ea54c1-e8fc-4fba-b7b5-3e47e5495c49")]
        public readonly InputSlot<bool> EnableZTest = new InputSlot<bool>();

        [Input(Guid = "0eb8c5cd-3c60-42f9-93c5-e2a7dae6b776")]
        public readonly InputSlot<System.Numerics.Vector3> LightPos = new InputSlot<System.Numerics.Vector3>();
    }
}

