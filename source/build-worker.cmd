@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "A2SRC=%ROOT%vendor\argon2-gpu"
if not exist "%ROOT%bin" mkdir "%ROOT%bin"
set VSCMD_SKIP_SENDTELEMETRY=1
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin\nvcc.exe" -O3 -std=c++17 -gencode arch=compute_75,code=sm_75 -gencode arch=compute_80,code=sm_80 -gencode arch=compute_86,code=sm_86 -gencode arch=compute_89,code=sm_89 -gencode arch=compute_90,code=sm_90 -gencode arch=compute_100,code=sm_100 -gencode arch=compute_103,code=sm_103 -gencode arch=compute_110,code=sm_110 -gencode arch=compute_120,code=sm_120 -gencode arch=compute_121,code=sm_121 -gencode arch=compute_75,code=compute_75 -DHAVE_CUDA=1 -DNDEBUG -I"%ROOT%." -I"%A2SRC%\include" -I"%A2SRC%\include\argon2-cuda" -I"%A2SRC%\include\argon2-gpu-common" -I"%A2SRC%\lib\argon2-cuda" -I"%A2SRC%\lib\argon2-gpu-common" -Xcompiler "/MT /O2 /EHsc" -o "%ROOT%..\bin\argon-gpu-worker.exe" "%ROOT%worker.cpp" "%A2SRC%\lib\argon2-gpu-common\argon2params.cpp" "%A2SRC%\lib\argon2-gpu-common\blake2b.cpp" "%A2SRC%\lib\argon2-cuda\device.cpp" "%A2SRC%\lib\argon2-cuda\globalcontext.cpp" "%A2SRC%\lib\argon2-cuda\programcontext.cpp" "%A2SRC%\lib\argon2-cuda\processingunit.cpp" "%A2SRC%\lib\argon2-cuda\kernels.cu" >"%ROOT%build-worker.log" 2>&1
if errorlevel 1 (type "%ROOT%build-worker.log" & exit /b 1)
echo GPU worker built successfully


