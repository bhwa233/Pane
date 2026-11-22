#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <thread>
#include <atomic>
#include <future>
#include <vector>
#include <algorithm>
#include <initializer_list>
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
namespace {
using Callback=void(__cdecl*)(unsigned);
struct Listener {std::thread thread;HWND window=nullptr;Callback callback=nullptr;bool removed=false;std::atomic<int> count{0};};
LRESULT CALLBACK Proc(HWND w,UINT m,WPARAM a,LPARAM b){
 auto p=reinterpret_cast<Listener*>(GetWindowLongPtrW(w,GWLP_USERDATA));
 if(m==WM_NCCREATE){p=static_cast<Listener*>(reinterpret_cast<CREATESTRUCTW*>(b)->lpCreateParams);SetWindowLongPtrW(w,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(p));}
 if(m==WM_CLIPBOARDUPDATE&&p){p->count++;p->callback(GetClipboardSequenceNumber());return 0;}
 if(m==WM_CLOSE){if(p)p->removed=RemoveClipboardFormatListener(w)!=0;DestroyWindow(w);PostQuitMessage(0);return 0;}
 return DefWindowProcW(w,m,a,b);
}
struct Apartment {HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();}};
ComPtr<IWICImagingFactory> Factory(){ComPtr<IWICImagingFactory> f;CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&f));return f;}
HRESULT Encode(IWICImagingFactory* f,IWICBitmapSource* source,IStream* stream){
 ComPtr<IWICBitmapEncoder> encoder;ComPtr<IWICBitmapFrameEncode> frame;HRESULT hr=f->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder);
 if(SUCCEEDED(hr))hr=encoder->Initialize(stream,WICBitmapEncoderNoCache);if(SUCCEEDED(hr))hr=encoder->CreateNewFrame(&frame,nullptr);
 if(SUCCEEDED(hr))hr=frame->Initialize(nullptr);if(SUCCEEDED(hr))hr=frame->WriteSource(source,nullptr);if(SUCCEEDED(hr))hr=frame->Commit();if(SUCCEEDED(hr))hr=encoder->Commit();return hr;
}
HRESULT Save(IWICImagingFactory* f,IWICBitmapSource* source,const wchar_t* path){ComPtr<IWICStream> stream;HRESULT hr=f->CreateStream(&stream);if(SUCCEEDED(hr))hr=stream->InitializeFromFilename(path,GENERIC_WRITE);return SUCCEEDED(hr)?Encode(f,source,stream.Get()):hr;}
// 用直通 BGRA 检查，避免预乘 alpha 抹掉透明像素的颜色。
bool RepairEmptyAlpha(BYTE* pixels,size_t size){
 for(size_t i=3;i<size;i+=4)if(pixels[i]!=0)return false;
 for(size_t i=3;i<size;i+=4)pixels[i]=255;
 return true;
}
HRESULT NormalizeImage(IWICImagingFactory* f,IWICBitmapSource* source,ComPtr<IWICBitmapSource>& result){
 UINT w=0,h=0;HRESULT hr=source->GetSize(&w,&h);if(FAILED(hr))return hr;
 if(!w||!h||static_cast<unsigned long long>(w)*h>MAXDWORD/4)return E_INVALIDARG;
 ComPtr<IWICFormatConverter> convert;hr=f->CreateFormatConverter(&convert);
 if(SUCCEEDED(hr))hr=convert->Initialize(source,GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom);
 if(FAILED(hr))return hr;
 const UINT size=w*h*4;std::vector<BYTE> pixels(size);hr=convert->CopyPixels(nullptr,w*4,size,pixels.data());if(FAILED(hr))return hr;
 // 任一像素有 alpha 时保留原始源；完整图和缩略图共用修正结果。
 if(!RepairEmptyAlpha(pixels.data(),pixels.size())){result=source;return S_OK;}
 ComPtr<IWICBitmap> bitmap;hr=f->CreateBitmapFromMemory(w,h,GUID_WICPixelFormat32bppBGRA,w*4,size,pixels.data(),&bitmap);
 if(SUCCEEDED(hr))result=bitmap;return hr;
}
HGLOBAL Memory(const void* data,size_t size,bool endWithZero=false){auto h=GlobalAlloc(endWithZero?GMEM_MOVEABLE|GMEM_ZEROINIT:GMEM_MOVEABLE,size+(endWithZero?1:0));if(!h)return nullptr;auto p=GlobalLock(h);if(!p){GlobalFree(h);return nullptr;}memcpy(p,data,size);GlobalUnlock(h);return h;}
bool Put(UINT format,HGLOBAL& memory){if(!SetClipboardData(format,memory))return false;memory=nullptr;return true;}
struct ClipboardBlock{UINT format;HGLOBAL& memory;};
bool WriteClipboard(int mode,std::initializer_list<ClipboardBlock> blocks,bool clearPartialImage=false){
 const UINT markerFormat=RegisterClipboardFormatW(mode==1?L"Pane.OwnClipboard":L"Pane.SourceClipboard");
 if(!markerFormat||std::any_of(blocks.begin(),blocks.end(),[](const ClipboardBlock& block){return block.memory&&!block.format;}))return false;
 DWORD one=1;HGLOBAL marker=Memory(&one,sizeof(one));HWND owner=CreateWindowW(L"STATIC",L"",0,0,0,0,0,HWND_MESSAGE,nullptr,GetModuleHandleW(nullptr),nullptr);
 bool opened=false;if(owner&&marker)for(int attempt=0;attempt<5&&!opened;attempt++){opened=OpenClipboard(owner)!=0;if(!opened)Sleep(15);}
 bool ok=opened&&EmptyClipboard()&&Put(markerFormat,marker);
 for(const auto& block:blocks){if(ok&&block.memory)ok=Put(block.format,block.memory);}
 if(opened&&!ok&&clearPartialImage&&!marker)EmptyClipboard();
 if(opened)CloseClipboard();if(owner)DestroyWindow(owner);if(marker)GlobalFree(marker);return ok;
}
}
API void* PaneClipListen(Callback callback){
 auto p=new Listener();p->callback=callback;std::promise<bool> ready;auto future=ready.get_future();
 p->thread=std::thread([p,&ready]{Apartment apartment;WNDCLASSW wc{};wc.lpfnWndProc=Proc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"PaneClipboard014";RegisterClassW(&wc);
 p->window=CreateWindowW(wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,p);
 bool ok=p->window&&AddClipboardFormatListener(p->window);ready.set_value(ok);
 if(ok){MSG msg;while(GetMessageW(&msg,nullptr,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}}else if(p->window)DestroyWindow(p->window);
 UnregisterClassW(wc.lpszClassName,wc.hInstance);
 });
 if(!future.get()){p->thread.join();delete p;return nullptr;}return p;
}
API int PaneClipStop(void* state,int* count){auto p=static_cast<Listener*>(state);PostMessageW(p->window,WM_CLOSE,0,0);p->thread.join();*count=p->count;bool ok=p->removed;delete p;return ok;}
// 调用方持有 OpenClipboard；不改变剪贴板。DIB 用 BMP 文件头交 WIC 解码。
static int ReadClipboardImage(void** output,int* length,UINT maxBytes,UINT maxPixels){
 *output=nullptr;*length=0;auto f=Factory();if(!f)return 0;ComPtr<IWICBitmapSource> source;ComPtr<IWICStream> input;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;
 UINT format=RegisterClipboardFormatW(L"PNG");if(!IsClipboardFormatAvailable(format))format=IsClipboardFormatAvailable(CF_DIBV5)?CF_DIBV5:CF_DIB;
 auto handle=GetClipboardData(format);if(!handle)return 0;size_t size=GlobalSize(handle);if(size>maxBytes||size>MAXDWORD-sizeof(BITMAPFILEHEADER))return 0;
 auto p=static_cast<BYTE*>(GlobalLock(handle));if(!p)return 0;
 std::vector<BYTE> bytes;
 if(format==CF_DIB||format==CF_DIBV5){
  if(size<sizeof(BITMAPINFOHEADER)){GlobalUnlock(handle);return 0;}auto header=reinterpret_cast<BITMAPINFOHEADER*>(p);
  if(header->biSize<sizeof(BITMAPINFOHEADER)||header->biSize>size){GlobalUnlock(handle);return 0;}
  DWORD colors=header->biClrUsed?header->biClrUsed:(header->biBitCount<=8?1u<<header->biBitCount:0);
  unsigned long long offset=sizeof(BITMAPFILEHEADER)+header->biSize+static_cast<unsigned long long>(colors)*4;
  if(header->biSize==40&&(header->biCompression==BI_BITFIELDS||header->biCompression==6))offset+=header->biCompression==6?16:12;
  if(offset>size+sizeof(BITMAPFILEHEADER)){GlobalUnlock(handle);return 0;}
  BITMAPFILEHEADER file{};file.bfType=0x4d42;file.bfSize=static_cast<DWORD>(size+sizeof(file));file.bfOffBits=static_cast<DWORD>(offset);
  bytes.resize(file.bfSize);memcpy(bytes.data(),&file,sizeof(file));memcpy(bytes.data()+sizeof(file),p,size);
 }else bytes.assign(p,p+size);GlobalUnlock(handle);
 HRESULT hr=f->CreateStream(&input);if(SUCCEEDED(hr))hr=input->InitializeFromMemory(bytes.data(),static_cast<DWORD>(bytes.size()));
 if(SUCCEEDED(hr))hr=f->CreateDecoderFromStream(input.Get(),nullptr,WICDecodeMetadataCacheOnLoad,&decoder);if(SUCCEEDED(hr))hr=decoder->GetFrame(0,&frame);
 if(SUCCEEDED(hr)&&maxPixels!=0){UINT width=0,height=0;hr=frame->GetSize(&width,&height);if(SUCCEEDED(hr)&&static_cast<unsigned long long>(width)*height>maxPixels)return 0;}
 if(SUCCEEDED(hr))hr=NormalizeImage(f.Get(),frame.Get(),source);
 ComPtr<IStream> encoded;if(SUCCEEDED(hr))hr=CreateStreamOnHGlobal(nullptr,TRUE,&encoded);if(SUCCEEDED(hr))hr=Encode(f.Get(),source.Get(),encoded.Get());
 STATSTG stats{};if(SUCCEEDED(hr))hr=encoded->Stat(&stats,STATFLAG_NONAME);if(FAILED(hr)||stats.cbSize.QuadPart>INT_MAX||stats.cbSize.QuadPart>maxBytes)return 0;
 *length=static_cast<int>(stats.cbSize.QuadPart);*output=CoTaskMemAlloc(*length);if(!*output)return 0;LARGE_INTEGER zero{};encoded->Seek(zero,STREAM_SEEK_SET,nullptr);ULONG read=0;hr=encoded->Read(*output,*length,&read);
 if(FAILED(hr)||read!=static_cast<ULONG>(*length)){CoTaskMemFree(*output);*output=nullptr;return 0;}return 1;
}
API int PaneClipImageAlloc(void** output,int* length){return ReadClipboardImage(output,length,MAXDWORD,0);}
API int PaneClipImageAllocLimited(void** output,int* length,UINT maxBytes,UINT maxPixels){return ReadClipboardImage(output,length,maxBytes,maxPixels);}
static int SaveDecodedImage(const wchar_t* input,const wchar_t* full,const wchar_t* thumb,UINT maxPixels,bool repairAlpha,int* width,int* height){
 Apartment apartment;auto f=Factory();if(!f)return 0;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICBitmapScaler> scale;
 HRESULT hr=f->CreateDecoderFromFilename(input,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder);if(SUCCEEDED(hr))hr=decoder->GetFrame(0,&frame);
 UINT w=0,h=0;if(SUCCEEDED(hr))hr=frame->GetSize(&w,&h);if(!w||!h||w>INT_MAX||h>INT_MAX||(maxPixels&&static_cast<unsigned long long>(w)*h>maxPixels))return 0;*width=static_cast<int>(w);*height=static_cast<int>(h);
 ComPtr<IWICBitmapSource> source;
 if(SUCCEEDED(hr)){if(repairAlpha)hr=NormalizeImage(f.Get(),frame.Get(),source);else source=frame;}
 if(SUCCEEDED(hr)&&full)hr=Save(f.Get(),source.Get(),full);if(SUCCEEDED(hr))hr=f->CreateBitmapScaler(&scale);double k=std::min(1.0,640.0/std::max(w,h));
 if(SUCCEEDED(hr))hr=scale->Initialize(source.Get(),std::max(1u,static_cast<UINT>(w*k)),std::max(1u,static_cast<UINT>(h*k)),WICBitmapInterpolationModeFant);
 if(SUCCEEDED(hr))hr=Save(f.Get(),scale.Get(),thumb);return SUCCEEDED(hr);
}
API int PaneClipSaveImage(const wchar_t* input,const wchar_t* full,const wchar_t* thumb,int* width,int* height){
 return SaveDecodedImage(input,full,thumb,0,true,width,height);
}
API int PaneClipSaveImageLimited(const wchar_t* input,const wchar_t* full,const wchar_t* thumb,UINT maxPixels,int* width,int* height){
 return SaveDecodedImage(input,full,thumb,maxPixels,false,width,height);
}
API int PaneClipWrite(const wchar_t* text,const wchar_t* image,const wchar_t* files,int fileChars,int mode){
 Apartment apartment;HGLOBAL data=nullptr,png=nullptr,dib=nullptr;UINT format=CF_UNICODETEXT;
 if(image){
  auto f=Factory();if(!f)return 0;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICFormatConverter> convert;
  HRESULT hr=f->CreateDecoderFromFilename(image,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder);if(SUCCEEDED(hr))hr=decoder->GetFrame(0,&frame);
  UINT w=0,h=0;if(SUCCEEDED(hr))hr=frame->GetSize(&w,&h);if(FAILED(hr)||!w||!h||static_cast<unsigned long long>(w)*h*4>MAXDWORD-sizeof(BITMAPV5HEADER))return 0;
  if(SUCCEEDED(hr))hr=f->CreateFormatConverter(&convert);if(SUCCEEDED(hr))hr=convert->Initialize(frame.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom);
  std::vector<BYTE> bytes(sizeof(BITMAPV5HEADER)+static_cast<size_t>(w)*h*4);auto header=reinterpret_cast<BITMAPV5HEADER*>(bytes.data());header->bV5Size=sizeof(*header);header->bV5Width=static_cast<LONG>(w);header->bV5Height=-static_cast<LONG>(h);header->bV5Planes=1;header->bV5BitCount=32;header->bV5Compression=BI_BITFIELDS;header->bV5RedMask=0xff0000;header->bV5GreenMask=0xff00;header->bV5BlueMask=0xff;header->bV5AlphaMask=0xff000000;header->bV5CSType=LCS_sRGB;
  if(SUCCEEDED(hr))hr=convert->CopyPixels(nullptr,w*4,w*h*4,bytes.data()+sizeof(*header));if(FAILED(hr))return 0;
  ComPtr<IStream> stream;STATSTG stats{};HGLOBAL encoded=nullptr;
  if(FAILED(CreateStreamOnHGlobal(nullptr,TRUE,&stream))||FAILED(Encode(f.Get(),frame.Get(),stream.Get()))||
     FAILED(stream->Stat(&stats,STATFLAG_NONAME))||FAILED(GetHGlobalFromStream(stream.Get(),&encoded)))return 0;
  auto ptr=GlobalLock(encoded);if(!ptr)return 0;
  png=Memory(ptr,static_cast<size_t>(stats.cbSize.QuadPart));GlobalUnlock(encoded);
  if(!png)return 0;
  data=Memory(bytes.data(),bytes.size());format=CF_DIBV5;
  // V5 保留直通 BGRA。传统 DIB 采用 Chromium 读取时预期的预乘颜色和自下而上行序。
  BITMAPINFOHEADER info{};info.biSize=sizeof(info);info.biWidth=w;info.biHeight=h;
  info.biPlanes=1;info.biBitCount=32;info.biCompression=BI_RGB;info.biSizeImage=w*h*4;
  auto pixels=bytes.data()+sizeof(*header);
  const size_t stride=static_cast<size_t>(w)*4;
  for(size_t i=0;i<info.biSizeImage;i+=4){
   BYTE alpha=pixels[i+3];
   if(alpha!=255)for(size_t c=0;c<3;c++)pixels[i+c]=static_cast<BYTE>((pixels[i+c]*alpha+127)/255);
  }
  for(UINT y=0;y<h/2;y++)std::swap_ranges(pixels+y*stride,pixels+(y+1)*stride,pixels+(h-1-y)*stride);
  auto begin=pixels-sizeof(info);memcpy(begin,&info,sizeof(info));
  dib=Memory(begin,sizeof(info)+info.biSizeImage);
 }else if(files&&fileChars>1){format=CF_HDROP;std::vector<BYTE> bytes(sizeof(DROPFILES)+static_cast<size_t>(fileChars)*2);auto drop=reinterpret_cast<DROPFILES*>(bytes.data());drop->pFiles=sizeof(DROPFILES);drop->fWide=TRUE;memcpy(bytes.data()+sizeof(DROPFILES),files,static_cast<size_t>(fileChars)*2);data=Memory(bytes.data(),bytes.size());}
 else if(text)data=Memory(text,(wcslen(text)+1)*2);
 bool ok=data&&(!image||dib)&&WriteClipboard(mode,{{png?RegisterClipboardFormatW(L"PNG"):0,png},{format,data},{CF_DIB,dib}},image!=nullptr);
 if(data)GlobalFree(data);if(png)GlobalFree(png);if(dib)GlobalFree(dib);return ok;
}
API int PaneClipWriteRichText(const wchar_t* text,const BYTE* html,int htmlSize,const BYTE* rtf,int rtfSize,int mode){
 constexpr int htmlLimit=8*1024*1024,rtfLimit=256*1024;
 if(!text||htmlSize<0||htmlSize>htmlLimit||rtfSize<0||rtfSize>rtfLimit||(htmlSize&&!html)||(rtfSize&&!rtf))return 0;
 HGLOBAL plain=Memory(text,(wcslen(text)+1)*sizeof(wchar_t));
 HGLOBAL htmlBlock=htmlSize?Memory(html,htmlSize,true):nullptr;
 HGLOBAL rtfBlock=rtfSize?Memory(rtf,rtfSize,true):nullptr;
 bool ok=plain&&(!htmlSize||htmlBlock)&&(!rtfSize||rtfBlock)&&WriteClipboard(mode,{
  {CF_UNICODETEXT,plain},{htmlBlock?RegisterClipboardFormatW(L"HTML Format"):0,htmlBlock},
  {rtfBlock?RegisterClipboardFormatW(L"Rich Text Format"):0,rtfBlock}});
 if(plain)GlobalFree(plain);if(htmlBlock)GlobalFree(htmlBlock);if(rtfBlock)GlobalFree(rtfBlock);return ok;
}
API int PaneClipSaveDialog(const wchar_t* source,const wchar_t* name){
 Apartment apartment;ComPtr<IFileSaveDialog> dialog;HRESULT hr=CoCreateInstance(CLSID_FileSaveDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog));
 COMDLG_FILTERSPEC filter{L"PNG",L"*.png"};if(SUCCEEDED(hr))hr=dialog->SetFileTypes(1,&filter);if(SUCCEEDED(hr))hr=dialog->SetDefaultExtension(L"png");if(SUCCEEDED(hr))hr=dialog->SetFileName(name);
 if(SUCCEEDED(hr))hr=dialog->Show(nullptr);if(hr==HRESULT_FROM_WIN32(ERROR_CANCELLED))return 2;
 ComPtr<IShellItem> result;if(SUCCEEDED(hr))hr=dialog->GetResult(&result);PWSTR path=nullptr;if(SUCCEEDED(hr))hr=result->GetDisplayName(SIGDN_FILESYSPATH,&path);
 if(SUCCEEDED(hr)&&!CopyFileW(source,path,FALSE))hr=HRESULT_FROM_WIN32(GetLastError());CoTaskMemFree(path);return SUCCEEDED(hr)?1:0;
}
API int PaneClipSourceIcon(const wchar_t* path,const wchar_t* output){
 Apartment apartment;auto f=Factory();if(!f)return 0;ComPtr<IShellItemImageFactory> item;
 HRESULT hr=SHCreateItemFromParsingName(path,nullptr,IID_PPV_ARGS(&item));HBITMAP handle=nullptr;
 if(SUCCEEDED(hr))hr=item->GetImage({32,32},SIIGBF_ICONONLY,&handle);ComPtr<IWICBitmap> bitmap;
 if(SUCCEEDED(hr))hr=f->CreateBitmapFromHBITMAP(handle,nullptr,WICBitmapUsePremultipliedAlpha,&bitmap);
 if(SUCCEEDED(hr))hr=Save(f.Get(),bitmap.Get(),output);if(handle)DeleteObject(handle);return SUCCEEDED(hr);
}
