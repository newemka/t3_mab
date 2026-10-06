namespace Types.Values;

[Guid("5503faad-b025-4d71-9dff-a5476c282a4f")]
public sealed class DirectIntValue :Instance<DirectIntValue>,IExtractedInput<int>
{
    [Output(Guid = "4e4e3f6f-33aa-43ba-b45c-2be56a2cc91f")]
    public readonly Slot<int> Result = new();

    public DirectIntValue()
    {
        Result.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        Result.Value = Int.GetValue(context);
    }
        

    public Slot<int> OutputSlot => Result;

    public void SetTypedInputValuesTo(int value, out IEnumerable<IInputSlot> changedInputs)
    {
        changedInputs = new[] { Int };
        Int.TypedInputValue.Value = value;
    }

    [Input(Guid = "2d0e4b7d-3e27-4b1b-8f95-5b4625174f93")]
    public readonly InputSlot<int> Int = new();

    [Input(Guid = "50fd8244-7d0d-4d14-91e5-0eada9968b44")]
    public readonly InputSlot<T3.Core.DataTypes.Vector.Int2> ColsRows = new();
}