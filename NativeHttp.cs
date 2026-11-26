using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeshViewer;

/// <summary>
/// HTTP-запрос силами WinHTTP — он есть в каждой Windows. Клиент из .NET (HttpClient) втягивал в AOT-образ
/// сокеты, TLS и HTTP-парсер — больше трёх мегабайт ради одного GET, а программе нужно только скачать
/// небольшой JSON и файл обновления.
/// </summary>
static class NativeHttp
{
    const int QueryStatusCode = 19, QueryFlagNumber = unchecked((int)0x20000000), FlagSecure = 0x00800000, HeaderAdd = 0x20000000;

    public static byte[] Get(string url, TimeSpan timeout)
    {
        Uri address = new(url);
        int milliseconds = (int)timeout.TotalMilliseconds;
        IntPtr session = Require(WinHttpOpen($"{App.Name}/{App.Version}", 0, IntPtr.Zero, IntPtr.Zero, 0), "открыть сетевую сессию");
        try
        {
            WinHttpSetTimeouts(session, milliseconds, milliseconds, milliseconds, milliseconds);
            IntPtr connection = Require(WinHttpConnect(session, address.Host, (ushort)address.Port, 0), "соединиться с " + address.Host);
            try
            {
                IntPtr request = Require(WinHttpOpenRequest(connection, "GET", address.PathAndQuery, null, IntPtr.Zero, IntPtr.Zero, address.Scheme == "https" ? FlagSecure : 0), "составить запрос");
                try
                {
                    WinHttpAddRequestHeaders(request, "Accept: application/vnd.github+json\r\n", -1, HeaderAdd);
                    Require(WinHttpSendRequest(request, IntPtr.Zero, 0, IntPtr.Zero, 0, 0, IntPtr.Zero), "отправить запрос");
                    Require(WinHttpReceiveResponse(request, IntPtr.Zero), "дождаться ответа");
                    int status = 0, length = sizeof(int);
                    Require(WinHttpQueryHeaders(request, QueryStatusCode | QueryFlagNumber, null, out status, ref length, IntPtr.Zero), "прочитать ответ");
                    if (status != 200) throw new InvalidOperationException($"сервер ответил кодом {status}");
                    using var body = new MemoryStream();
                    while (true)
                    {
                        Require(WinHttpQueryDataAvailable(request, out int available), "узнать размер ответа");
                        if (available == 0) break;
                        var chunk = new byte[available];
                        Require(WinHttpReadData(request, chunk, available, out int read), "прочитать ответ");
                        body.Write(chunk, 0, read);
                    }
                    return body.ToArray();
                }
                finally
                {
                    WinHttpCloseHandle(request);
                }
            }
            finally
            {
                WinHttpCloseHandle(connection);
            }
        }
        finally
        {
            WinHttpCloseHandle(session);
        }
    }

    static IntPtr Require(IntPtr handle, string what) => handle != IntPtr.Zero ? handle : throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось " + what);

    static void Require(bool done, string what)
    {
        if (!done) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось " + what);
    }

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WinHttpOpen(string agent, int access, IntPtr proxy, IntPtr bypass, int flags);

    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpSetTimeouts(IntPtr session, int resolve, int connect, int send, int receive);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WinHttpConnect(IntPtr session, string server, ushort port, int reserved);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WinHttpOpenRequest(IntPtr session, string verb, string path, string? version, IntPtr referer, IntPtr types, int flags);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WinHttpAddRequestHeaders(IntPtr request, string headers, int length, int options);

    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpSendRequest(IntPtr request, IntPtr headers, int headerLength, IntPtr optional, int optionalLength, int totalLength, IntPtr context);

    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WinHttpQueryHeaders(IntPtr request, int query, string? name, out int value, ref int length, IntPtr index);

    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpQueryDataAvailable(IntPtr request, out int available);

    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpReadData(IntPtr request, byte[] buffer, int length, out int read);

    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpCloseHandle(IntPtr handle);
}
