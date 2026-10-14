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

using System.Diagnostics;
using System.Net.Sockets;

namespace Roxa.Core;

/// <summary>تست TCP مستقیم (بدون پروکسی) — برای مرتب‌سازی اولیه.</summary>
public static class LatencyTester
{
    public static async Task<(bool Ok, int Ms)> TcpAsync(string host, int port, int timeoutMs = 3000, TestRouteScope? scope = null)
    {
        bool created = false;
        if (scope == null)
        {
            scope = await TestRouteBypass.BeginAsync(new[] { host });
            created = true;
        }
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(timeoutMs);
            var sw = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(host, port, cts.Token);
                sw.Stop();
                return (true, (int)sw.Elapsed.TotalMilliseconds);
            }
            catch { return (false, 0); }
        }
        finally
        {
            if (created) scope.Dispose();
        }
    }
}
