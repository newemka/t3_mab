using Lib.image.transform;
using SharpDX;

namespace Lib.numbers.floats.process;

[Guid("0be17d51-f2d0-46d1-ac8f-caf2348c2f10")]
internal sealed class ListStatistics : Instance<ListStatistics>
{
    [Output(Guid = "ba2b2386-c9cb-4f5b-af61-83f877aa9e72")]
    public readonly Slot<float> Result = new();

    public ListStatistics()
    {
        Result.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var list = Input.GetValue(context);
        var operation = Operation.GetValue(context);

        if (list == null || list.Count == 0)
        {
            Result.Value = 0f;
            return;
        }

        float result = 0f;

        switch (operation)
        {
            case 0: // Sum
                result = CalculateSum(list);
                break;
            case 1: // Maximum
                result = CalculateMax(list);
                break;
            case 2: // Minimum
                result = CalculateMin(list);
                break;
            case 3: // Mean (Average)
                result = CalculateMean(list);
                break;
            case 4: // Median
                result = CalculateMedian(list);
                break;
            default:
                result = 0f;
                break;
        }

        Result.Value = result;
    }

    private float CalculateSum(List<float> list)
    {
        var sum = 0f;
        foreach (var value in list)
        {
            sum += value;
        }
        return sum;
    }

    private float CalculateMax(List<float> list)
    {
        var max = float.MinValue;
        foreach (var value in list)
        {
            if (value > max)
                max = value;
        }
        return max;
    }

    private float CalculateMin(List<float> list)
    {
        var min = float.MaxValue;
        foreach (var value in list)
        {
            if (value < min)
                min = value;
        }
        return min;
    }

    private float CalculateMean(List<float> list)
    {
        var sum = CalculateSum(list);
        return sum / list.Count;
    }

    private float CalculateMedian(List<float> list)
    {
        var sortedList = new List<float>(list);
        sortedList.Sort();

        var count = sortedList.Count;
        if (count % 2 == 0)
        {
            var mid = count / 2;
            return (sortedList[mid - 1] + sortedList[mid]) / 2f;
        }
        else
        {
            return sortedList[count / 2];
        }
    }

    private enum OperationType
    {
        Sum = 0,
        Maximum = 1,
        Minimum = 2,
        Mean = 3,
        Median = 4
    }

    [Input(Guid = "bcbe1f13-c169-4eae-b1fe-b6e1c648737a", MappedType = typeof(OperationType))]
    public readonly InputSlot<int> Operation = new(0);

    [Input(Guid = "21eb8bd5-fe93-4f6f-8d31-78770e82d271")]
    public readonly InputSlot<List<float>> Input = new(new List<float>(20));
}