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

using System.Runtime.InteropServices;

namespace Roxa.Core;

internal static class WfpNative
{
    internal static readonly Guid ConnectLayerV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    internal static readonly Guid ConnectLayerV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    internal static readonly Guid SubLayerKey = new("8b4f2c1e-6a3d-4e9b-9c7f-1d5e8a3b6c42");

    private static readonly Guid CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid CondRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid CondRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");

    private const uint FwpUint16 = 2;
    private const uint FwpUint32 = 3;
    private const uint FwpUint64 = 4;
    private const uint FwpByteArray16 = 11;
    private const uint FwpBlob = 12;
    private const uint FwpRange = 258;
    private const int MatchEqual = 0;
    private const int MatchRange = 5;
    private const uint ActionBlock = 0x1001;
    private const uint ActionPermit = 0x1002;
    private const uint SessionDynamic = 1;
    private const uint AuthnDefault = 0xFFFFFFFF;
    private const long PermitWeight = 65535;
    private const long BlockWeight = 1;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FWPValue
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public ushort u16;
        [FieldOffset(8)] public uint u32;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBlob
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct FilterCondition
    {
        [FieldOffset(0)] public Guid fieldKey;
        [FieldOffset(16)] public int matchType;
        [FieldOffset(24)] public FWPValue value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Range0
    {
        public FWPValue valueLow;
        public FWPValue valueHigh;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct FwAction
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(4)] public Guid guid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct FwpmFilter
    {
        [FieldOffset(0)] public Guid filterKey;
        [FieldOffset(16)] public IntPtr ddName;
        [FieldOffset(24)] public IntPtr ddDesc;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public IntPtr providerKey;
        [FieldOffset(48)] public ByteBlob providerData;
        [FieldOffset(64)] public Guid layerKey;
        [FieldOffset(80)] public Guid subLayerKey;
        [FieldOffset(96)] public FWPValue weight;
        [FieldOffset(112)] public uint numFilterConditions;
        [FieldOffset(120)] public IntPtr filterCondition;
        [FieldOffset(128)] public FwAction action;
        [FieldOffset(152)] public ulong rawContext;
        [FieldOffset(168)] public IntPtr reserved;
        [FieldOffset(176)] public ulong filterId;
        [FieldOffset(184)] public FWPValue effectiveWeight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct FwpmSession
    {
        [FieldOffset(0)] public Guid sessionKey;
        [FieldOffset(16)] public IntPtr ddName;
        [FieldOffset(24)] public IntPtr ddDesc;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(36)] public uint txnWaitTimeoutInMSec;
        [FieldOffset(40)] public uint processId;
        [FieldOffset(48)] public IntPtr sid;
        [FieldOffset(56)] public IntPtr username;
        [FieldOffset(64)] public int kernelMode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct FwpmSubLayer
    {
        [FieldOffset(0)] public Guid subLayerKey;
        [FieldOffset(16)] public IntPtr ddName;
        [FieldOffset(24)] public IntPtr ddDesc;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public IntPtr providerKey;
        [FieldOffset(48)] public ByteBlob providerData;
        [FieldOffset(64)] public ushort weight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, ref FwpmSession session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FwpmSubLayer subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FwpmFilter filter, IntPtr sd, out ulong id);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref IntPtr mem);

    internal static uint OpenSession(out IntPtr engine)
    {
        var session = new FwpmSession
        {
            sessionKey = Guid.NewGuid(),
            flags = SessionDynamic,
        };
        return FwpmEngineOpen0(null, AuthnDefault, IntPtr.Zero, ref session, out engine);
    }

    internal static void CloseSession(ref IntPtr engine)
    {
        if (engine == IntPtr.Zero) return;
        FwpmEngineClose0(engine);
        engine = IntPtr.Zero;
    }

    internal static uint AddSubLayer(IntPtr engine)
    {
        var sub = new FwpmSubLayer
        {
            subLayerKey = SubLayerKey,
            ddName = Marshal.StringToHGlobalUni("RoxaKillSw"),
            weight = ushort.MaxValue,
        };
        try
        {
            return FwpmSubLayerAdd0(engine, ref sub, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(sub.ddName);
        }
    }

    internal static uint AddBlock(IntPtr engine, Guid layer) =>
        AddFilter(engine, layer, IntPtr.Zero, 0, ActionBlock, BlockWeight, "RoxaKillSwBlock");

    internal static uint AddPermitApp(IntPtr engine, Guid layer, string exePath)
    {
        uint rc = FwpmGetAppIdFromFileName0(exePath, out IntPtr appId);
        if (rc != 0) return rc;
        if (appId == IntPtr.Zero) return 1;
        IntPtr condPtr = Marshal.AllocHGlobal(40);
        try
        {
            var cond = new FilterCondition
            {
                fieldKey = CondAppId,
                matchType = MatchEqual,
                value = new FWPValue { type = (int)FwpBlob, ptr = appId },
            };
            Marshal.StructureToPtr(cond, condPtr, false);
            return AddFilter(engine, layer, condPtr, 1, ActionPermit, PermitWeight, "RoxaKillSwApp");
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
            FwpmFreeMemory0(ref appId);
        }
    }

    internal static uint AddPermitPort(IntPtr engine, Guid layer, ushort port)
    {
        IntPtr condPtr = Marshal.AllocHGlobal(40);
        try
        {
            var cond = new FilterCondition
            {
                fieldKey = CondRemotePort,
                matchType = MatchEqual,
                value = new FWPValue { type = (int)FwpUint16, u16 = port },
            };
            Marshal.StructureToPtr(cond, condPtr, false);
            return AddFilter(engine, layer, condPtr, 1, ActionPermit, PermitWeight, "RoxaKillSwPort");
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
        }
    }

    internal static uint AddPermitRange4(IntPtr engine, uint lowNet, uint highNet)
    {
        IntPtr rangePtr = Marshal.AllocHGlobal(32);
        IntPtr condPtr = Marshal.AllocHGlobal(40);
        try
        {
            var range = new Range0
            {
                valueLow = new FWPValue { type = (int)FwpUint32, u32 = lowNet },
                valueHigh = new FWPValue { type = (int)FwpUint32, u32 = highNet },
            };
            Marshal.StructureToPtr(range, rangePtr, false);
            var cond = new FilterCondition
            {
                fieldKey = CondRemoteAddress,
                matchType = MatchRange,
                value = new FWPValue { type = (int)FwpRange, ptr = rangePtr },
            };
            Marshal.StructureToPtr(cond, condPtr, false);
            return AddFilter(engine, ConnectLayerV4, condPtr, 1, ActionPermit, PermitWeight, "RoxaKillSwNet4");
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
            Marshal.FreeHGlobal(rangePtr);
        }
    }

    internal static uint AddPermitRange6(IntPtr engine, byte[] low, byte[] high)
    {
        IntPtr lowPtr = Marshal.AllocHGlobal(16);
        IntPtr highPtr = Marshal.AllocHGlobal(16);
        IntPtr rangePtr = Marshal.AllocHGlobal(32);
        IntPtr condPtr = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.Copy(low, 0, lowPtr, 16);
            Marshal.Copy(high, 0, highPtr, 16);
            var range = new Range0
            {
                valueLow = new FWPValue { type = (int)FwpByteArray16, ptr = lowPtr },
                valueHigh = new FWPValue { type = (int)FwpByteArray16, ptr = highPtr },
            };
            Marshal.StructureToPtr(range, rangePtr, false);
            var cond = new FilterCondition
            {
                fieldKey = CondRemoteAddress,
                matchType = MatchRange,
                value = new FWPValue { type = (int)FwpRange, ptr = rangePtr },
            };
            Marshal.StructureToPtr(cond, condPtr, false);
            return AddFilter(engine, ConnectLayerV6, condPtr, 1, ActionPermit, PermitWeight, "RoxaKillSwNet6");
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
            Marshal.FreeHGlobal(rangePtr);
            Marshal.FreeHGlobal(lowPtr);
            Marshal.FreeHGlobal(highPtr);
        }
    }

    private static uint AddFilter(IntPtr engine, Guid layer, IntPtr conditions, uint conditionCount, uint action, long weight, string name)
    {
        IntPtr weightPtr = Marshal.AllocHGlobal(8);
        IntPtr namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            Marshal.WriteInt64(weightPtr, weight);
            var filter = new FwpmFilter
            {
                filterKey = Guid.NewGuid(),
                ddName = namePtr,
                layerKey = layer,
                subLayerKey = SubLayerKey,
                weight = new FWPValue { type = (int)FwpUint64, ptr = weightPtr },
                numFilterConditions = conditionCount,
                filterCondition = conditions,
                action = new FwAction { type = action },
                effectiveWeight = new FWPValue { type = 0 },
            };
            return FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(weightPtr);
            Marshal.FreeHGlobal(namePtr);
        }
    }
}
