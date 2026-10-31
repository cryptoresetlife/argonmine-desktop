// Fixed Argonmine Argon2id initialization/finalization. No networking or wallet keys.
#pragma once
#include <cuda_runtime.h>
#include <stdint.h>
namespace fixedargon {
__constant__ unsigned char header[120];
__constant__ uint64_t iv[8]={
 0x6a09e667f3bcc908ULL,0xbb67ae8584caa73bULL,0x3c6ef372fe94f82bULL,0xa54ff53a5f1d36f1ULL,
 0x510e527fade682d1ULL,0x9b05688c2b3e6c1fULL,0x1f83d9abfb41bd6bULL,0x5be0cd19137e2179ULL};
__constant__ unsigned char sigma[12][16]={
 {0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15},
 {14,10,4,8,9,15,13,6,1,12,0,2,11,7,5,3},
 {11,8,12,0,5,2,15,13,10,14,3,6,7,1,9,4},
 {7,9,3,1,13,12,11,14,2,6,5,10,4,0,15,8},
 {9,0,5,7,2,4,10,15,14,1,11,12,6,8,3,13},
 {2,12,6,10,0,11,8,3,4,13,7,5,15,14,1,9},
 {12,5,1,15,14,13,4,10,0,7,6,3,9,2,8,11},
 {13,11,7,14,12,1,3,9,5,0,15,4,8,6,2,10},
 {6,15,14,9,11,3,0,8,12,2,13,7,1,4,10,5},
 {10,2,8,4,7,6,1,5,15,11,9,14,3,12,13,0},
 {0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15},
 {14,10,4,8,9,15,13,6,1,12,0,2,11,7,5,3}};
__device__ __forceinline__ uint64_t rotr(uint64_t v,int s){return (v>>s)|(v<<(64-s));}
__device__ __forceinline__ void mix(uint64_t &a,uint64_t &b,uint64_t &c,uint64_t &d,uint64_t x,uint64_t y){
 a=a+b+x;d=rotr(d^a,32);c+=d;b=rotr(b^c,24);a=a+b+y;d=rotr(d^a,16);c+=d;b=rotr(b^c,63);
}
__device__ void blake(unsigned char *out,int outlen,const unsigned char *in,int len,bool prefix=false,uint32_t prefixValue=0){
 uint64_t h[8];
 #pragma unroll
 for(int i=0;i<8;i++)h[i]=iv[i];
 h[0]^=0x01010000U|uint32_t(outlen);
 const int total=len+(prefix?4:0);uint64_t counter=0;
 for(int offset=0;;offset+=128){
  const int count=(total-offset<128)?total-offset:128;uint64_t m[16]={0};
  for(int i=0;i<count;i++){
   int at=offset+i;unsigned char v=(prefix&&at<4)?static_cast<unsigned char>(prefixValue>>(8*at)):in[at-(prefix?4:0)];
   m[i>>3]|=uint64_t(v)<<(8*(i&7));
  }
  counter+=count;uint64_t v[16];
  #pragma unroll
  for(int i=0;i<8;i++){v[i]=h[i];v[i+8]=iv[i];}
  v[12]^=counter;bool last=offset+count==total;if(last)v[14]=~v[14];
  #pragma unroll
  for(int r=0;r<12;r++){
   const unsigned char *s=sigma[r];
   mix(v[0],v[4],v[8],v[12],m[s[0]],m[s[1]]);mix(v[1],v[5],v[9],v[13],m[s[2]],m[s[3]]);
   mix(v[2],v[6],v[10],v[14],m[s[4]],m[s[5]]);mix(v[3],v[7],v[11],v[15],m[s[6]],m[s[7]]);
   mix(v[0],v[5],v[10],v[15],m[s[8]],m[s[9]]);mix(v[1],v[6],v[11],v[12],m[s[10]],m[s[11]]);
   mix(v[2],v[7],v[8],v[13],m[s[12]],m[s[13]]);mix(v[3],v[4],v[9],v[14],m[s[14]],m[s[15]]);
  }
  #pragma unroll
  for(int i=0;i<8;i++)h[i]^=v[i]^v[i+8];
  if(last)break;
 }
 for(int i=0;i<outlen;i++)out[i]=static_cast<unsigned char>(h[i>>3]>>(8*(i&7)));
}
__device__ __forceinline__ void store32(unsigned char *p,uint32_t n){for(int i=0;i<4;i++)p[i]=static_cast<unsigned char>(n>>(i*8));}
__global__ void initialize(unsigned char *memory,uint32_t batch,uint64_t base,uint32_t padding){
 unsigned int job=blockIdx.x*blockDim.x+threadIdx.x;if(job>=batch)return;
 unsigned char msg[192]={0};store32(msg,1);store32(msg+4,32);store32(msg+8,8192);store32(msg+12,1);store32(msg+16,19);store32(msg+20,2);store32(msg+24,120);
 for(int i=0;i<120;i++)msg[28+i]=header[i];uint64_t nonce=base+uint64_t(job);
 for(int i=0;i<8;i++)msg[28+112+i]=static_cast<unsigned char>(nonce>>(8*i));
 const char salt[]="Argonmine/pow/v1";constexpr int slen=sizeof(salt)-1;store32(msg+148,slen);
 for(int i=0;i<slen;i++)msg[152+i]=salt[i];
 store32(msg+152+slen,0);store32(msg+156+slen,0);
 unsigned char seed[72]={0};blake(seed,64,msg,160+slen);
 unsigned char *jobmem=memory+uint64_t(job)*(8192+padding)*1024;
 for(int block=0;block<2;block++){
  store32(seed+64,block);store32(seed+68,0);unsigned char v[64];blake(v,64,seed,72,true,1024);
  unsigned char *dest=jobmem+block*1024;
  for(int k=0;k<30;k++){for(int j=0;j<32;j++)dest[k*32+j]=v[j];if(k!=29)blake(v,64,v,64);}
  blake(dest+960,64,v,64);
 }
}
__global__ void finalize(const unsigned char *memory,unsigned char *hashes,uint32_t batch,uint32_t padding){
 unsigned int job=blockIdx.x*blockDim.x+threadIdx.x;if(job>=batch)return;
 blake(hashes+job*32,32,memory+(uint64_t(job)*(8192+padding)+8191)*1024,1024,true,32);
}
} // namespace fixedargon
