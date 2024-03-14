using System;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Interfaces;
using T3.Core.Operator.Slots;

namespace T3.Operators.Types.Id_8360f1b5_72cc_4bea_8311_537fd7d7b263
{
    public class TwistMesh : Instance<TwistMesh>
,ITransformable
    {
        [Output(Guid = "c65bdd7f-21da-463b-9881-ee992286e303")]
        public readonly TransformCallbackSlot<T3.Core.DataTypes.MeshBuffers> Result = new();
        
        public TwistMesh()
        {
            Result.TransformableOp = this;
        }        
        
        IInputSlot ITransformable.TranslationInput => Translation;
        IInputSlot ITransformable.RotationInput => Rotation;
        IInputSlot ITransformable.ScaleInput => Size;
        public Action<Instance, EvaluationContext> TransformCallback { get; set; }

        [Input(Guid = "ffc01d58-99fb-4290-9a20-3ad89463137d")]
        public readonly InputSlot<T3.Core.DataTypes.MeshBuffers> Mesh = new();

        [Input(Guid = "a25fb1d4-43f2-43b5-b72e-084b32ffcfae")]
        public readonly InputSlot<System.Numerics.Vector3> Translation = new();

        [Input(Guid = "325a1eed-40db-45df-a6ac-a7e0a5875c49")]
        public readonly InputSlot<System.Numerics.Vector3> Rotation = new();

        [Input(Guid = "ff444bb6-bfb5-46db-9d22-a4cdc5c185b6")]
        public readonly InputSlot<System.Numerics.Vector3> Size = new();

        [Input(Guid = "3cd26bc9-7033-490a-9d40-4a4c7fbfc612")]
        public readonly InputSlot<int> TwistAxis = new InputSlot<int>();

        [Input(Guid = "8dc864d0-d24f-4c4b-9e32-03bff786edb5")]
        public readonly InputSlot<float> Amount = new InputSlot<float>();

        [Input(Guid = "e5df7fab-30f8-4f88-9309-68ee13a10380")]
        public readonly InputSlot<bool> UseSelected = new InputSlot<bool>();

        [Input(Guid = "61432b75-1756-4270-ad58-26f7a8504db2")]
        public readonly InputSlot<float> Shift = new InputSlot<float>();
        
        
        
        
        
        
        
        
        
    }
}

