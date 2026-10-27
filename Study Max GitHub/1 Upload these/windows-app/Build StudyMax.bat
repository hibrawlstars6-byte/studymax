@echo off
cd /d "%~dp0"
rem close any running copy first so the exe can be replaced
taskkill /im StudyMax.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /optimize /target:winexe /win32icon:studymax.ico /resource:standby.html,standby.html /out:StudyMax.exe /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Media.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.Threading.Tasks.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll StudyMax.cs > build.txt 2>&1
if errorlevel 1 goto fail
rem Desktop shortcut named "StudyMax" (shortcuts never show .exe)
> "%TEMP%\studymax-link.vbs" echo Set sh = CreateObject("WScript.Shell")
>> "%TEMP%\studymax-link.vbs" echo Set s = sh.CreateShortcut(sh.SpecialFolders("Desktop") ^& "\StudyMax.lnk")
>> "%TEMP%\studymax-link.vbs" echo s.TargetPath = "%~dp0StudyMax.exe"
>> "%TEMP%\studymax-link.vbs" echo s.WorkingDirectory = "%~dp0"
>> "%TEMP%\studymax-link.vbs" echo s.IconLocation = "%~dp0StudyMax.exe,0"
>> "%TEMP%\studymax-link.vbs" echo s.Save
cscript //nologo "%TEMP%\studymax-link.vbs"
del "%TEMP%\studymax-link.vbs" >nul 2>&1
echo Done! Open StudyMax on your Desktop
pause
exit /b
:fail
type build.txt
echo.
echo Build failed. Tell Claude, it can read build.txt.
pause
