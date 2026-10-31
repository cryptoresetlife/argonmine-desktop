#include <cuda_runtime.h>
#include <argon2-cuda/globalcontext.h>
#include <argon2-cuda/programcontext.h>
#include <argon2-cuda/processingunit.h>
#include <argon2-gpu-common/argon2params.h>
#include <chrono>
#include <array>
#include <iostream>
#include <sstream>
#include <iomanip>
#include <string>
#include <cstring>
#include <stdexcept>
#include <vector>

static int unhex(char c) {
    if(c>='0' && c<='9') return c-'0';
    if(c>='a' && c<='f') return c-'a'+10;
    if(c>='A' && c<='F') return c-'A'+10;
    throw std::runtime_error("invalid header hex");
}
static std::string hex(const unsigned char *p, size_t n) {
    const char *digits="0123456789abcdef";
    std::string s; s.reserve(n*2);
    for(size_t i=0;i<n;i++){s+=digits[p[i]>>4];s+=digits[p[i]&15];}
    return s;
}
int main(int argc,char **argv) {
 try {
    size_t batch=argc>1?std::stoul(argv[1]):256;
    const std::string mode=argc>2?argv[2]:"gpu";
    const bool legacy=mode=="legacy";
    const uint32_t padding=mode.find("linear")!=std::string::npos?0:1;
    if(batch<1 || batch>2048 || (batch&(batch-1))) throw std::runtime_error("batch must be a power of two, 1..2048");
    cudaDeviceProp prop{};
    if(cudaSetDevice(0)!=cudaSuccess || cudaGetDeviceProperties(&prop,0)!=cudaSuccess) throw std::runtime_error("CUDA device unavailable");
    std::string uid=hex(reinterpret_cast<unsigned char*>(prop.uuid.bytes),16);
    if(prop.major*10+prop.minor < 75) throw std::runtime_error("this CUDA build requires NVIDIA compute capability 7.5 or newer");
    size_t freeBytes=0,totalBytes=0;
    if(cudaMemGetInfo(&freeBytes,&totalBytes)!=cudaSuccess) throw std::runtime_error("cannot inspect available CUDA allocation memory");
    const size_t reserve=512ULL*1024*1024;
    while(batch>64 && batch*(8192ULL*1024+1024)+reserve>freeBytes) batch/=2;
    if(batch*(8192ULL*1024+1024)+reserve>freeBytes) throw std::runtime_error("not enough free GPU memory; close other GPU applications");
    argon2::cuda::GlobalContext global;
    const auto &device=global.getAllDevices().at(0);
    argon2::cuda::ProgramContext program(&global,{device},argon2::ARGON2_ID,argon2::ARGON2_VERSION_13);
    const char *salt="Argonmine/pow/v1";
    argon2::Argon2Params params(32,salt,std::strlen(salt),nullptr,0,nullptr,0,1,8192,1);
    argon2::cuda::ProcessingUnit unit(&program,&params,&device,batch,true,true);
    auto jobOption=mode.find("-j");if(jobOption!=std::string::npos)unit.setJobsPerBlock(std::stoul(mode.substr(jobOption+2)));
    std::cout<<"{\"ready\":true,\"device\":\"NVIDIA CUDA\",\"uuid\":\""<<uid<<"\",\"batch\":"<<batch<<",\"jobsPerBlock\":"<<unit.getJobsPerBlock()<<",\"paddingKiB\":"<<padding<<"}"<<std::endl;
    std::string line;
    while(std::getline(std::cin,line)) {
        if(line=="quit") break;
        std::istringstream input(line); std::string headerHex,extra; uint64_t nonce;
        if(!(input>>headerHex>>nonce) || (input>>extra) || headerHex.size()!=240) throw std::runtime_error("expected 120-byte header hex and uint64 nonce");
        std::array<unsigned char,120> header{};
        for(size_t i=0;i<120;i++) header[i]=static_cast<unsigned char>((unhex(headerHex[2*i])<<4)|unhex(headerHex[2*i+1]));
        auto start=std::chrono::steady_clock::now();
        if(!legacy) unit.setFixedHeaders(header.data(),nonce,padding);
        if(legacy) for(size_t i=0;i<batch;i++) {
            uint64_t n=nonce+static_cast<uint64_t>(i);
            for(int k=0;k<8;k++) header[112+k]=static_cast<unsigned char>(n>>(k*8));
            unit.setPassword(i,header.data(),header.size());
        }
        auto initialized=std::chrono::steady_clock::now();
        unit.beginProcessing(); float memoryKernelMS=unit.endProcessing();
        auto computed=std::chrono::steady_clock::now();
        std::vector<unsigned char> allHashes(batch*32);
        if(!legacy) unit.getFixedHashes(allHashes.data());
        if(legacy) for(size_t i=0;i<batch;i++) unit.getHash(i,allHashes.data()+i*32);
        auto finalized=std::chrono::steady_clock::now();
        std::ostringstream hashes;
        for(size_t i=0;i<batch;i++) {
            if(i) hashes<<',';
            hashes<<'"'<<hex(allHashes.data()+i*32,32)<<'"';
        }
        double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count();
        double initMS=std::chrono::duration<double,std::milli>(initialized-start).count();
        double computeMS=std::chrono::duration<double,std::milli>(computed-initialized).count();
        double finalMS=std::chrono::duration<double,std::milli>(finalized-computed).count();
        std::cout<<"{\"hashes\":["<<hashes.str()<<"],\"ms\":"<<ms<<",\"initMS\":"<<initMS<<",\"computeMS\":"<<computeMS<<",\"finalMS\":"<<finalMS<<",\"memoryKernelMS\":"<<memoryKernelMS<<"}"<<std::endl;
    }
    return 0;
 } catch(const std::exception &e) { std::cerr<<"FATAL: "<<e.what()<<std::endl; return 1; }
}

