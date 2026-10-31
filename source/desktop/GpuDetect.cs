using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

// Static CUDA Driver metadata only. No context, allocation, kernel, NVML or temperatures.
static class GpuDetect {
    [StructLayout(LayoutKind.Sequential)] struct UUID { [MarshalAs(UnmanagedType.ByValArray,SizeConst=16)] public byte[] Bytes; }
    [DllImport("nvcuda.dll")] static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGet(out int device,int ordinal);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGetName(StringBuilder name,int len,int device);
    [DllImport("nvcuda.dll",EntryPoint="cuDeviceTotalMem_v2")] static extern int cuDeviceTotalMem(out UIntPtr bytes,int device);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGetAttribute(out int value,int attribute,int device);
    [DllImport("nvcuda.dll",EntryPoint="cuDeviceGetUuid_v2")] static extern int cuDeviceGetUuidV2(ref UUID uuid,int device);
    [DllImport("nvcuda.dll",EntryPoint="cuDeviceGetUuid")] static extern int cuDeviceGetUuid(ref UUID uuid,int device);
    static void Check(int result) { if(result!=0)throw new Exception("CUDA driver error "+result); }
    public static int Main() {
        var json=new JavaScriptSerializer();
        try {
            Check(cuInit(0)); int count; Check(cuDeviceGetCount(out count)); var devices=new List<object>();
            for(int i=0;i<count;i++) {
                int d,major,minor; UIntPtr memory; Check(cuDeviceGet(out d,i)); var name=new StringBuilder(256);
                Check(cuDeviceGetName(name,256,d)); Check(cuDeviceTotalMem(out memory,d));
                Check(cuDeviceGetAttribute(out major,75,d)); Check(cuDeviceGetAttribute(out minor,76,d));
                var uuid=new UUID{Bytes=new byte[16]};
                try {Check(cuDeviceGetUuidV2(ref uuid,d));}catch(EntryPointNotFoundException){Check(cuDeviceGetUuid(ref uuid,d));}
                string hex=BitConverter.ToString(uuid.Bytes).Replace("-","").ToLowerInvariant();
                devices.Add(new {Index=i,Name=name.ToString(),UUID=hex,MemoryBytes=checked((long)memory.ToUInt64()),Cc=major*10+minor,Supported=(major*10+minor>=75 && memory.ToUInt64()>=2UL*1024*1024*1024)});
            }
            Console.WriteLine(json.Serialize(new {Devices=devices,Error=""})); return 0;
        } catch(Exception ex) { Console.WriteLine(json.Serialize(new {Devices=new object[0],Error=ex is DllNotFoundException?"未找到NVIDIA驱动，请安装或更新NVIDIA驱动。":ex.Message}));return 1; }
    }
}
