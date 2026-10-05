@echo off
rem 重新编译 SqlStructViewer（无需 Visual Studio / .NET SDK，仅用 Windows 自带的 C# 编译器）
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] 找不到 csc.exe: %CSC%
  echo 该程序依赖 Windows 自带的 .NET Framework 4.x 编译器。
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:SqlStructViewer.exe SqlStructViewer.cs
if errorlevel 1 (
  echo [ERROR] 编译失败
  exit /b 1
)
echo [OK] 已生成 SqlStructViewer.exe
endlocal
