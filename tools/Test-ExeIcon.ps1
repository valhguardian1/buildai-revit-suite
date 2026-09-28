param([string]$ExePath,[string]$IconPath)
$ErrorActionPreference='Stop'
if(-not ('BuildAIIconResources' -as [type])) {
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class BuildAIIconResources {
  delegate bool EnumProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
  [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
  [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
  [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern bool EnumResourceNames(IntPtr module,IntPtr type,EnumProc callback,IntPtr param);
  [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
  [DllImport("kernel32")] static extern uint SizeofResource(IntPtr module,IntPtr resource);
  [DllImport("kernel32")] static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
  [DllImport("kernel32")] static extern IntPtr LockResource(IntPtr resource);
  public static byte[][] Read(string path) {
    var module=LoadLibraryEx(path,IntPtr.Zero,2);
    if(module==IntPtr.Zero) throw new Exception("Cannot read EXE resources: "+Marshal.GetLastWin32Error());
    var images=new List<byte[]>();
    try {
      EnumProc callback=(m,t,n,p)=>{
        var resource=FindResource(m,n,t);
        var bytes=new byte[SizeofResource(m,resource)];
        Marshal.Copy(LockResource(LoadResource(m,resource)),bytes,0,bytes.Length);
        images.Add(bytes); return true;
      };
      EnumResourceNames(module,new IntPtr(3),callback,IntPtr.Zero);
      return images.ToArray();
    } finally {FreeLibrary(module);}
  }
}
'@
}
$ico=[IO.File]::ReadAllBytes((Resolve-Path $IconPath).Path)
$resources=[BuildAIIconResources]::Read((Resolve-Path $ExePath).Path)
$encoded=@($resources | ForEach-Object {[Convert]::ToBase64String($_)})
$count=[BitConverter]::ToUInt16($ico,4)
for($i=0;$i -lt $count;$i++) {
  $size=[BitConverter]::ToUInt32($ico,6+16*$i+8)
  $offset=[BitConverter]::ToUInt32($ico,6+16*$i+12)
  $bytes=New-Object byte[] $size
  [Array]::Copy($ico,$offset,$bytes,0,$size)
  if([Convert]::ToBase64String($bytes) -notin $encoded){throw "EXE is missing BuildAI ICO image $i."}
}
Write-Host "EXE contains all $count BuildAI ICO images, byte-for-byte."
