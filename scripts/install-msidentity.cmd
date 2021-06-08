set VERSION=1.0.0-dev
set NUPKG=artifacts\packages\Debug\Shipping\

rem save current directory path, push it on a stack and switch to it.
pushd %~dp0
call cd ..

rem save base project path.
set SRC_DIR=%cd%

rem kill any lingering dotnet.exe's that would interfere with dotnet packing and tool installing.
call taskkill /f /im dotnet.exe
call rd /Q /S artifacts

call dotnet pack MSIdentityScaffolding.slnf
call dotnet tool uninstall -g Microsoft.dotnet-msidentity
call dotnet tool install -g Microsoft.dotnet-msidentity --add-source %SRC_DIR%\%NUPKG% --version %VERSION%

rem return to directory at the top of the stack.
popd