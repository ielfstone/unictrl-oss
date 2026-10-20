using System.Runtime.InteropServices;
namespace Unictrl;

/// Swallows only a paste that can fetch a current remote file offer; replays it after download.
internal sealed class FilePasteHook : IDisposable
{
    private const int Tag=0x5546494C;
    private readonly Func<bool> paste;
    private readonly Callback callback;
    private nint hook;
    private uint threadId;
    private volatile bool disposed;
    private bool swallowed;
    private delegate nint Callback(int code,nint wParam,nint lParam);
    public FilePasteHook(Func<bool> paste)
    {
        this.paste=paste;callback=Handle;
        new Thread(()=>{
            threadId=GetCurrentThreadId();if(disposed)return;
            hook=SetWindowsHookExW(13,callback,GetModuleHandleW(null),0);
            if(hook==0)return;
            while(!disposed&&Native.GetMessageW(out var message,0,0,0)>0){Native.TranslateMessage(ref message);Native.DispatchMessageW(ref message);}
            UnhookWindowsHookEx(hook);
        }){IsBackground=true,Name="Unictrl file paste"}.Start();
    }
    private nint Handle(int code,nint message,nint data)
    {
        if(code>=0)
        {
            var key=Marshal.PtrToStructure<Key>(data);
            if(key.Code==0x56&&key.Extra!=(nuint)Tag)
            {
                var up=message==0x101||message==0x105;
                if(up&&swallowed){swallowed=false;return 1;}
                if(!up&&(GetAsyncKeyState(0x11)&0x8000)!=0&&(GetAsyncKeyState(0x12)&0x8000)==0&&(GetAsyncKeyState(0x10)&0x8000)==0)
                {if(swallowed||paste()){swallowed=true;return 1;}}
            }
        }
        return CallNextHookEx(hook,code,message,data);
    }
    public static void Replay()
    {
        var inputs=new[]{Event(0x11,false),Event(0x56,false),Event(0x56,true),Event(0x11,true)};
        Native.SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Native.Input>());
    }
    private static Native.Input Event(ushort key,bool up)=>new(){Type=1,Data={Keyboard=new Native.KeyboardInput{VirtualKey=key,Flags=up?2u:0u,ExtraInfo=Tag}}};
    public void Dispose(){disposed=true;if(threadId!=0)PostThreadMessageW(threadId,0x12,0,0);}
    [StructLayout(LayoutKind.Sequential)]private struct Key{public uint Code,Scan,Flags,Time;public nuint Extra;}
    [DllImport("user32.dll")]private static extern nint SetWindowsHookExW(int id,Callback callback,nint module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nint message,nint data);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandleW(string? module);
    [DllImport("user32.dll")]private static extern bool PostThreadMessageW(uint thread,uint message,nint w,nint l);
}
