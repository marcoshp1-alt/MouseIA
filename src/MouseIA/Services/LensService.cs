using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using MouseIA.Core;

namespace MouseIA.Services;

/// <summary>
/// Searches an image with Google Lens in the default browser. A small local page posts the picture
/// to Lens' upload form, just like dropping it on lens.google.com; the results also offer AI Mode.
/// </summary>
public static class LensService
{
    private static string Folder => Path.Combine(AppSettings.DefaultFolder, "lens");

    public static void Search(BitmapSource image)
    {
        var fitted = ImageUtil.Fit(ImageUtil.Flatten(image), 1000);
        // If the browser blocks the automatic upload, the picture is ready to paste.
        try { Clipboard.SetImage(fitted); } catch (Exception) { }

        Directory.CreateDirectory(Folder);
        CleanOld();
        var path = Path.Combine(Folder, $"lens-{DateTime.Now:yyyyMMdd-HHmmss-fff}.html");
        File.WriteAllText(path, Page(Convert.ToBase64String(ImageUtil.ToPng(fitted))), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void CleanOld()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Folder, "lens-*.html"))
                if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromHours(1)) File.Delete(file);
        }
        catch (Exception) { }
    }

    private static string Page(string pngBase64)
    {
        string lang = WebUtility.HtmlEncode(Loc.IsPortuguese ? "pt-BR" : "en");
        string searching = WebUtility.HtmlEncode(Loc.T("lens_sending"));
        string again = WebUtility.HtmlEncode(Loc.T("lens_again"));
        string failed = System.Text.Json.JsonSerializer.Serialize(Loc.T("lens_failed"));
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $$"""
<!doctype html>
<html lang="{{lang}}"><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">
<title>MouseIA · Google Lens</title>
<style>
html,body{height:100%;margin:0;background:radial-gradient(circle at 50% 30%,#1b2a44,#0d1220);color:#e8eefc;font:15px 'Segoe UI',system-ui,sans-serif}
body{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px}
img{max-width:380px;max-height:240px;border-radius:16px;box-shadow:0 0 40px #5aa9ff55}
#b{font:inherit;padding:10px 22px;border:0;border-radius:22px;background:linear-gradient(90deg,#5aa9ff,#8f7bff);color:#fff;cursor:pointer}
</style></head><body>
<img id="p" alt=""><div id="s">{{searching}}</div><button id="b" hidden>{{again}}</button>
<form id="f" method="POST" enctype="multipart/form-data" action="https://lens.google.com/v3/upload?hl={{lang}}&amp;st={{stamp}}">
<input type="file" name="encoded_image" id="i" hidden></form>
<script>
const data="{{pngBase64}}";
document.getElementById('p').src='data:image/png;base64,'+data;
const button=document.getElementById('b'), status=document.getElementById('s');
function send(){
  try{
    const bytes=Uint8Array.from(atob(data),c=>c.charCodeAt(0));
    const t=new DataTransfer();
    t.items.add(new File([bytes],'mouseia.png',{type:'image/png'}));
    document.getElementById('i').files=t.files;
    try{sessionStorage.setItem('sent','1');}catch(e){}
    document.getElementById('f').submit();
  }catch(e){status.textContent={{failed}};button.hidden=false;}
}
button.onclick=send;
let sent=false;try{sent=sessionStorage.getItem('sent')==='1';}catch(e){}
if(sent){status.textContent='';button.hidden=false;}else{setTimeout(()=>{button.hidden=false;},5000);send();}
</script></body></html>
""";
    }
}
