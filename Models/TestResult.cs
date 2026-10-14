// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

namespace Roxa.Models;

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
