namespace XrayClient.Models;

public enum TestStatus { Ok, Timeout, Error, Cancelled }

/// <summary>نتیجه یک تست شبکه. Extra برای دیاگنوستیک (warmup، jitter و غیره).</summary>
public sealed class TestResult
{
    public TestStatus Status { get; init; }
    public double Value { get; init; }
    public Dictionary<string, string> Extra { get; init; } = new();

    public bool Ok => Status == TestStatus.Ok;

    public static TestResult Success(double value, Dictionary<string, string>? extra = null)
        => new() { Status = TestStatus.Ok, Value = value, Extra = extra ?? new() };

    public static TestResult Fail(TestStatus status, Dictionary<string, string>? extra = null)
        => new() { Status = status == TestStatus.Ok ? TestStatus.Error : status, Extra = extra ?? new() };

    public static TestResult Cancelled(Dictionary<string, string>? extra = null)
        => new() { Status = TestStatus.Cancelled, Extra = extra ?? new() };

    public void SetExtra(string key, string value) => Extra[key] = value;
}
