set VERSION=6.0.0-dev
set DEFAULT_NUPKG_PATH=%userprofile%\.nuget\packages
set NUPKG=artifacts\packages\Debug\Shipping\

rem save current directory path, push it on a stack and switch to it.
pushd %~dp0
call cd ..

rem save base project path.
set SRC_DIR=%cd%

rem kill any lingering dotnet.exe's that would interfere with dotnet packing and tool installing.
call taskkill /f /im dotnet.exe
call rd /Q /S artifacts

call dotnet pack Scaffolding.slnf
call dotnet tool uninstall -g dotnet-aspnet-codegenerator


call cd %DEFAULT_NUPKG_PATH%
call rd /Q /S microsoft.visualstudio.web.codegeneration
call rd /Q /S microsoft.visualstudio.web.codegeneration.contracts
call rd /Q /S microsoft.visualstudio.web.codegeneration.core
call rd /Q /S microsoft.visualstudio.web.codegeneration.design
call rd /Q /S microsoft.visualstudio.web.codegeneration.entityframeworkcore
call rd /Q /S microsoft.visualstudio.web.codegeneration.templating
call rd /Q /S microsoft.visualstudio.web.codegeneration.utils
call rd /Q /S microsoft.visualstudio.web.codegenerators.mvc

call cd  %SRC_DIR%\%NUPKG% 
call dotnet tool install -g dotnet-aspnet-codegenerator --add-source %SRC_DIR%\%NUPKG% --version %VERSION%

rem return to directory at the top of the stack.
popd