@echo off
rem Builds DevGameSwitch.exe with the C# compiler that ships with Windows.
rem Edit the service list at the top of MainForm in Program.cs, then re-run this.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ ^
  /out:DevGameSwitch.exe /win32manifest:app.manifest /win32icon:app.ico ^
  /r:System.ServiceProcess.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll ^
  Program.cs
