@echo off
chcp 65001 >nul
REM 用系统自带的 .NET Framework 编译器生成启动器，不需要安装 VS 或 dotnet SDK
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" -nologo -target:exe -platform:x64 -codepage:65001 -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll -out:AliyaWeb-Launcher.exe AliyaWebLauncher.cs
if errorlevel 1 (
  echo 编译失败
  exit /b 1
)
echo 已生成 AliyaWeb-Launcher.exe
