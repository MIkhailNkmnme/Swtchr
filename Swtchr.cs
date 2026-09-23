// Swtchr.cs — аналог dotSwitcher. Клавиша или сочетание модификаторов (по умолчанию левый Shift, можно
// например Ctrl+Shift), нажатые и отпущенные без других клавиш:
//   • есть выделение   → перебивается выделенный текст (через буфер обмена);
//   • первое нажатие   → последнее набранное слово;
//   • второе подряд    → вся строка (набранное с последнего Enter / клика);
//   • третье подряд    → всё обратно, как было.
// «Подряд» — если между нажатиями ничего не набирали и не кликали. После конверсии системная раскладка переключается на «правильную».
// Автопереключение (auto_switch=1): без словарей, по статистике троек букв (Ngram, таблицы внизу файла —
// собраны из строк интерфейса Windows ru-RU / en-US). Слово оценивается «как есть» и «в другой раскладке»;
// если второе заметно правдоподобнее — перебивается на ходу (с 3-й буквы, чем короче — тем строже)
// или на пробеле. Слова из 2–3 букв — по списку частых (Ngram.ShortRu / ShortEn). Отмена — клавиша-триггер
// сразу после этого; такое слово запоминается в Swtchr.ignore.txt и больше само не перебивается.
// Настройки — Swtchr.ini рядом с exe (создаётся при первом запуске), правятся из трея.
//
// RDP: локальный экземпляр не трогает окна mstsc/msrdc (ignore_rdp=1) — на удалённой машине
// запускается свой Swtchr, иначе слово перебьётся дважды.
//
// Сборка (csc входит в Windows, ничего ставить не нужно): build.cmd
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ---------------------------------------------------------------- WinAPI
static class N {
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B;
    public const int WM_INPUTLANGCHANGEREQUEST = 0x50;
    public const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;
    public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10,
        VK_CONTROL = 0x11, VK_MENU = 0x12, VK_PAUSE = 0x13, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B,
        VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26,
        VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_INSERT = 0x2D, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
        VK_APPS = 0x5D, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1,
        VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
    [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int n, IntPtr[] list);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ToUnicodeEx(uint vk, uint sc, byte[] state, StringBuilder buf, int size, uint flags, IntPtr hkl);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScanEx(char c, IntPtr hkl);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
    // буфер обмена напрямую, без OLE (Clipboard из WinForms при занятом буфере ждёт секундами)
    public const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint fmt);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr h);

    public static bool Down(int vk) { return GetAsyncKeyState(vk) < 0; }
}

// ---------------------------------------------------------------- таблицы раскладок
// ---------------------------------------------------------------- модель троек букв
// Оценка правдоподобия слова: средний log P(буква | две предыдущие), ^ — начало слова, $ — конец.
class Ngram {
    readonly Dictionary<string, int> tri = new Dictionary<string, int>();
    readonly Dictionary<string, int> ctx = new Dictionary<string, int>();
    readonly int v;

    // data: «тройка + вес (hex) + пробел» подряд
    Ngram(string data, int alphabet) {
        v = alphabet + 1;
        for (int i = 0; i + 3 < data.Length; ) {
            int j = data.IndexOf(' ', i + 3);
            string t = data.Substring(i, 3);
            int n = Convert.ToInt32(data.Substring(i + 3, j - i - 3), 16);
            tri[t] = n;
            int c; ctx.TryGetValue(t.Substring(0, 2), out c); ctx[t.Substring(0, 2)] = c + n;
            i = j + 1;
        }
    }
    public static readonly Ngram Ru = new Ngram(NgramData.Ru, 33), En = new Ngram(NgramData.En, 26);

    // word — строчными, ё → е; full — слово закончено (учитываем и его конец)
    public double Score(string word, bool full) {
        string x = "^" + word + (full ? "$" : "");
        double sum = 0; int n = 0;
        for (int i = 0; i + 3 <= x.Length; i++) {
            int t, c;
            tri.TryGetValue(x.Substring(i, 3), out t);
            ctx.TryGetValue(x.Substring(i, 2), out c);
            sum += Math.Log((t + 0.01) / (c + 0.01 * v));
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    // короткие частые слова, как они выглядят в чужой раскладке:
    // ShortRu — русские, набранные латиницей; ShortEn — английские, набранные кириллицей.
    // Пересечения с настоящими словами (vs = мы, шт = in) исключены.
    public static readonly HashSet<string> ShortRu = new HashSet<string>(
        "yt yf gj bp jn lj yj nj ;t yb ,s jy jyf jyj jyb b[ tuj tt t` rfr xnj 'nj dct dc` nfr nfv nen ult rnj xtv ytn e;t tot to` ghb lkz bkb ,tp gjl yfl ghj hfp ldf nhb vjq vjz vjb vj` ndjq yfi dfi djn djy jq fuf ofc x` xt bv tq tve yfv dfv yfc dcz dtcm jx vyt vtyz nt,t nt,z ct,z njn nf njq ntv xnj, rjn kjk jr f; e; pf kb ds ns lf ye dfc nt".Split(' '));
    public static readonly HashSet<string> ShortEn = new HashSet<string>(
        "еру ещ шы ща щк фтв щт ше фы иу ша тщ вщ пщ гз ыщ цу ру ьу ьн гы нщг фку цфы тще ащк сфт фдд рфы иге нуы пуе ыуе гыу туц щту щге тщц рщц црн црщ рш щл шеы мшф зук фтн ьфн щаа щгк ршы рук ршь ыру ерун еруь ершы ерфе цшер акщь црфе црут цшдд нщгк рфму фт фе ин".Split(' '));
}

static class Layout {
    const string EN_L = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./";
    const string RU_L = "ё1234567890-=йцукенгшщзхъ\\фывапролджэячсмитьбю.";
    const string EN_U = "~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:\"ZXCVBNM<>?";
    const string RU_U = "Ё!\"№;%:?*()_+ЙЦУКЕНГШЩЗХЪ/ФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,";
    const string EN_LETTERS = "qwertyuiopasdfghjklzxcvbnmQWERTYUIOPASDFGHJKLZXCVBNM";
    const string RU_LETTERS = "йцукенгшщзхъфывапролджэячсмитьбюёЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮЁ";

    static readonly Dictionary<char, char> En2Ru = new Dictionary<char, char>();
    static readonly Dictionary<char, char> Ru2En = new Dictionary<char, char>();

    static Layout() {
        for (int i = 0; i < EN_L.Length; i++) { En2Ru[EN_L[i]] = RU_L[i]; Ru2En[RU_L[i]] = EN_L[i]; }
        for (int i = 0; i < EN_U.Length; i++) { En2Ru[EN_U[i]] = RU_U[i]; Ru2En[RU_U[i]] = EN_U[i]; }
    }

    public static bool IsEn(char c) { return EN_LETTERS.IndexOf(c) >= 0; }
    public static bool IsRu(char c) { return RU_LETTERS.IndexOf(c) >= 0; }
    // знак переводится в букву другой раскладки (',' → 'б', 'ж' ← ';' и т.п.)
    public static bool Maps(char c, int from) {
        char o; return (from == 1 ? En2Ru : Ru2En).TryGetValue(c, out o);
    }

    // 1 — латиница, 2 — кириллица, 0 — нет букв
    static int NextLang(string s, int from) {
        for (int j = from; j < s.Length; j++) { if (IsEn(s[j])) return 1; if (IsRu(s[j])) return 2; }
        return 0;
    }

    // Буквы перебиваются по своему языку; знаки — по контексту последней буквы,
    // а если букв ещё не было — по ближайшей следующей.
    public static string Convert(string s) {
        var sb = new StringBuilder(s.Length);
        int last = 0;
        for (int i = 0; i < s.Length; i++) {
            char c = s[i], o;
            if (IsEn(c)) { sb.Append(En2Ru.TryGetValue(c, out o) ? o : c); last = 1; }
            else if (IsRu(c)) { sb.Append(Ru2En.TryGetValue(c, out o) ? o : c); last = 2; }
            else {
                int ctx = last != 0 ? last : NextLang(s, i + 1);
                if (ctx == 1 && En2Ru.TryGetValue(c, out o)) sb.Append(o);
                else if (ctx == 2 && Ru2En.TryGetValue(c, out o)) sb.Append(o);
                else sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // Каким был текст: 1 — латиница (значит, после конверсии нужна русская раскладка), 2 — кириллица
    public static int Direction(string s) {
        int en = 0, ru = 0;
        foreach (char c in s) { if (IsEn(c)) en++; else if (IsRu(c)) ru++; }
        if (en == 0 && ru == 0) return 0;
        return en >= ru ? 1 : 2;
    }
}

// ---------------------------------------------------------------- приложение
class App : ApplicationContext {
    static Mutex mutex;

    [STAThread]
    static void Main() {
        bool created;
        mutex = new Mutex(true, "Swtchr_single_instance_mutex", out created);
        if (!created) { MessageBox.Show("Swtchr уже запущен (иконка в трее).", "Swtchr"); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new App());
    }

    readonly string dir = AppDomain.CurrentDomain.BaseDirectory;
    string iniPath;
    // настройки
    string keyText = "LShift";
    int[][] keyGroups;             // триггер: зажаты должны быть все группы («Ctrl+Shift»), в группе — любая клавиша («Shift» = левый или правый)
    int[] keyVks;                  // все VK триггера одним списком
    readonly HashSet<int> held = new HashSet<int>();   // физически зажатые клавиши триггера
    bool switchLayout = true, ignoreRdp = true, autoSwitch = true;
    bool debug;
    static string logPath;         // debug=1 в ini — журнал в Swtchr.log
    void SetDebug(bool on) { debug = on; logPath = on ? Path.Combine(dir, "Swtchr.log") : null; }
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static void Log(string msg) {
        if (logPath == null) return;
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8); } catch { }
    }

    // автопереключение
    string ignorePath;
    readonly HashSet<string> ignoreWords = new HashSet<string>();   // перебиты обратно вручную — сами больше не трогаем
    string lastAutoBuf, lastAutoWord;   // буфер сразу после автоперебивки и исходное слово — чтобы узнать отмену
    bool lastAutoFull;                  // перебивка была на пробеле (иначе — на ходу, слово ещё набирается)
    int doneAt = -1;                    // начало слова, которое уже решено (перебито на ходу / отменено) — до пробела не трогаем

    // клавиша-триггер: 1 — слово, 2 — строка, 3 — обратно; цикл идёт, пока между нажатиями не было ввода
    int inputGen;                       // растёт на каждое нажатие клавиши (кроме триггера) и клик
    int keyStep, keyGen;                // шаг цикла и inputGen после него
    string keyOrig;                     // режим набранного: buf до первого шага
    string selOrig, selConv;            // режим выделения (ничего не набрано): слово до и после первого шага
    string selLine;                     // строка до второго шага — для возврата

    // пробел (space_switch=1): второй пробел после слова — перебить слово, третий — всю строку,
    // четвёртый — вернуть как было. Любая другая клавиша цикл сбрасывает.
    bool spaceKey = true;
    int spaceCycle;                     // 0 — нет цикла, 1 — перебито слово, 2 — перебита строка
    string cycleOrig;                   // строка до первого шага (без завершающего пробела)
    string cycleBuf;                    // buf после последнего шага — цикл продолжается, только пока он не менялся
    string cycleEndBuf;                 // buf после возврата — следующий пробел уже обычный
    volatile bool autoRunning;          // идёт автоперебивка: нажатия пользователя копим и повторяем после
    readonly Queue<int[]> pending = new Queue<int[]>();   // { vk, scan, shift }
    readonly HashSet<int> swallowed = new HashSet<int>(); // проглоченные нажатия — их отпускание тоже глотаем

    NotifyIcon tray;
    N.HookProc kbProc, msProc;
    IntPtr kbHook, msHook;

    // набранный текст с последнего Enter / навигации / клика / смены окна
    readonly StringBuilder buf = new StringBuilder();
    IntPtr lastWnd; bool isRdp, isConsole;
    volatile bool busy;            // идёт конверсия — свои же нажатия не отслеживаем

    // триггер: сочетание собрано, без других клавиш до отпускания
    bool keyDown, armed;
    bool dirty;                    // пока зажата клавиша триггера, нажималось что-то ещё (Ctrl+C, клик)

    // эвристика «есть выделение»: протяжка/двойной клик мышью, Shift+стрелки, Ctrl+A
    bool selected; N.POINT mDownPt; uint mDownTime; int clickCount;

    public App() {
        iniPath = Path.Combine(dir, "Swtchr.ini");
        var cw = new NativeWindow(); cw.CreateHandle(new CreateParams()); clipWnd = cw.Handle;
        ignorePath = Path.Combine(dir, "Swtchr.ignore.txt");
        LoadConfig();
        LoadIgnore();
        Log("старт: авто=" + autoSwitch + ", клавиша=" + keyText);
        tray = new NotifyIcon();
        tray.Icon = MakeIcon();
        tray.Visible = true;
        RebuildTray();
        tray.DoubleClick += delegate { ShowSettings(); };

        kbProc = KbHook; msProc = MsHook;
        IntPtr hmod = N.GetModuleHandle(null);
        kbHook = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, kbProc, hmod, 0);
        msHook = N.SetWindowsHookEx(N.WH_MOUSE_LL, msProc, hmod, 0);
        if (kbHook == IntPtr.Zero) {
            MessageBox.Show("Не удалось установить хук клавиатуры (ошибка " + Marshal.GetLastWin32Error() + ").", "Swtchr");
            ExitThread(); return;
        }
        Application.ApplicationExit += delegate {
            if (kbHook != IntPtr.Zero) N.UnhookWindowsHookEx(kbHook);
            if (msHook != IntPtr.Zero) N.UnhookWindowsHookEx(msHook);
            tray.Visible = false; tray.Dispose();
        };
    }

    // ------------------------------------------------------------ настройки
    string IniText() {
        return
        "; Swtchr — настройки. Правятся из трея («Настройки…»); файл можно править и руками,\r\n" +
        "; тогда после правки перезапустите Swtchr.\r\n" +
        "\r\n" +
        "; Клавиша-триггер: нажать и отпустить, ничего другого не нажимая.\r\n" +
        "; Допустимые имена: LShift RShift Shift  LCtrl RCtrl Ctrl  LAlt RAlt Alt  LWin RWin Win\r\n" +
        "; (без L/R — подходит любая из двух). Сочетание — через «+», например Ctrl+Shift\r\n" +
        "key=" + keyText + "\r\n" +
        "\r\n" +
        "; Нажатие — последнее слово, второе подряд — вся строка, третье — всё обратно\r\n" +
        "\r\n" +
        "; переключать системную раскладку после конверсии (1 / 0)\r\n" +
        "switch_layout=" + (switchLayout ? "1" : "0") + "\r\n" +
        "\r\n" +
        "; не работать, когда активно окно RDP-клиента (mstsc / msrdc): там работает Swtchr удалённой машины (1 / 0)\r\n" +
        "ignore_rdp=" + (ignoreRdp ? "1" : "0") + "\r\n" +
        "\r\n" +
        "; автопереключение: после пробела слово, набранное не в той раскладке, перебивается само (1 / 0).\r\n" +
        "; Без словарей — по сочетаниям букв, можно на ходу, не дожидаясь пробела. В консолях не работает. Отмена — клавиша-триггер сразу после\r\n" +
        "; перебивки; такие слова копятся в Swtchr.ignore.txt\r\n" +
        "auto_switch=" + (autoSwitch ? "1" : "0") + "\r\n" +
        "\r\n" +
        "; пробел ещё раз после слова — перебить слово, ещё раз — всю строку, ещё раз — всё обратно (1 / 0).\r\n" +
        "; Включается либо auto_switch, либо space_switch: при обоих = 1 работает auto_switch\r\n" +
        "space_switch=" + (spaceKey ? "1" : "0") + "\r\n" +
        "\r\n" +
        "; журнал в Swtchr.log — для отладки (1 / 0)\r\n" +
        "debug=" + (debug ? "1" : "0") + "\r\n";
    }

    void SaveConfig() {
        try { File.WriteAllText(iniPath, IniText(), Encoding.UTF8); }
        catch (Exception ex) { MessageBox.Show("Не удалось записать Swtchr.ini: " + ex.Message, "Swtchr"); }
    }

    void LoadConfig() {
        var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        cfg["key"] = "LShift"; cfg["switch_layout"] = "1"; cfg["ignore_rdp"] = "1"; cfg["auto_switch"] = "1"; cfg["space_switch"] = "1"; cfg["debug"] = "0";
        try {
            if (!File.Exists(iniPath)) File.WriteAllText(iniPath, IniText(), Encoding.UTF8);
            else foreach (string raw in File.ReadAllLines(iniPath, Encoding.UTF8)) {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) cfg[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        } catch (Exception ex) { MessageBox.Show("Swtchr.ini: " + ex.Message, "Swtchr"); }
        try { SetKey(cfg["key"]); }
        catch (Exception ex) {
            MessageBox.Show("Ошибка в Swtchr.ini (key): " + ex.Message + "\r\nВзят LShift.", "Swtchr");
            SetKey("LShift");
        }
        switchLayout = cfg["switch_layout"] != "0";
        ignoreRdp = cfg["ignore_rdp"] != "0";
        autoSwitch = cfg["auto_switch"] != "0";
        spaceKey = cfg["space_switch"] != "0" && !autoSwitch;   // включён может быть только один из режимов
        debug = cfg["debug"] == "1";
        SetDebug(debug);
    }

    void LoadIgnore() {
        try { if (File.Exists(ignorePath)) foreach (string w in File.ReadAllLines(ignorePath, Encoding.UTF8)) if (w.Trim().Length > 0) ignoreWords.Add(w.Trim().ToLowerInvariant()); }
        catch { }
    }
    void AddIgnore(string w) {
        w = w.ToLowerInvariant();
        if (!ignoreWords.Add(w)) return;
        try { File.AppendAllText(ignorePath, w + "\r\n", Encoding.UTF8); } catch { }
    }

    void SetKey(string text) {
        var groups = new List<int[]>(); var all = new List<int>(); var names = new List<string>();
        foreach (string part in text.Split('+')) {
            int[] g = ModKeys(part);
            foreach (int vk in g) if (all.Contains(vk)) throw new Exception("клавиша «" + part.Trim() + "» указана дважды");
            groups.Add(g); all.AddRange(g); names.Add(part.Trim());
        }
        keyGroups = groups.ToArray(); keyVks = all.ToArray(); keyText = string.Join("+", names.ToArray());
        held.Clear(); keyDown = armed = false;
    }

    bool ComboDown() {
        foreach (int[] g in keyGroups) {
            bool any = false;
            foreach (int vk in g) if (held.Contains(vk)) { any = true; break; }
            if (!any) return false;
        }
        return true;
    }

    static int[] ModKeys(string name) {
        switch (name.Trim().ToLowerInvariant()) {
            case "ctrl": case "control": return new[] { N.VK_LCONTROL, N.VK_RCONTROL };
            case "lctrl": return new[] { N.VK_LCONTROL };
            case "rctrl": return new[] { N.VK_RCONTROL };
            case "shift": return new[] { N.VK_LSHIFT, N.VK_RSHIFT };
            case "lshift": return new[] { N.VK_LSHIFT };
            case "rshift": return new[] { N.VK_RSHIFT };
            case "alt": return new[] { N.VK_LMENU, N.VK_RMENU };
            case "lalt": return new[] { N.VK_LMENU };
            case "ralt": return new[] { N.VK_RMENU };
            case "win": return new[] { N.VK_LWIN, N.VK_RWIN };
            case "lwin": return new[] { N.VK_LWIN };
            case "rwin": return new[] { N.VK_RWIN };
        }
        throw new Exception("неизвестная клавиша «" + name.Trim() + "»");
    }

    // ------------------------------------------------------------ трей
    void RebuildTray() {
        var menu = new ContextMenuStrip();
        var head = new ToolStripMenuItem("Swtchr"); head.Enabled = false; menu.Items.Add(head);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(keyText + ":  слово / выделение").Enabled = false;
        menu.Items.Add(keyText + " ещё раз:  вся строка, ещё раз:  обратно").Enabled = false;
        if (spaceKey) menu.Items.Add("Пробел ×2 / ×3 / ×4:  слово / строка / обратно").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        var auto = new ToolStripMenuItem("Автопереключение");
        auto.Checked = autoSwitch;
        auto.Click += delegate { autoSwitch = !autoSwitch; if (autoSwitch) spaceKey = false; spaceCycle = 0; SaveConfig(); RebuildTray(); };
        menu.Items.Add(auto);
        var sp = new ToolStripMenuItem("Перебивка двойным пробелом");
        sp.Checked = spaceKey;
        sp.Click += delegate { spaceKey = !spaceKey; if (spaceKey) autoSwitch = false; spaceCycle = 0; SaveConfig(); RebuildTray(); };
        menu.Items.Add(sp);
        menu.Items.Add("Настройки…", null, delegate { ShowSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, delegate { ExitThread(); });
        tray.ContextMenuStrip = menu;
        string tip = "Swtchr: " + keyText + (autoSwitch ? ", авто" : "");
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    bool settingsOpen;
    void ShowSettings() {
        if (settingsOpen) return;
        settingsOpen = true;
        try {
            using (var f = new SettingsForm(keyText, switchLayout, ignoreRdp, autoSwitch, spaceKey, IsAutostart(), debug, tray.Icon)) {
                if (f.ShowDialog() != DialogResult.OK) return;
                try { SetKey(f.Key); }
                catch (Exception ex) { MessageBox.Show("Клавиша: " + ex.Message, "Swtchr"); return; }
                switchLayout = f.SwitchLayout; ignoreRdp = f.IgnoreRdp; autoSwitch = f.AutoSwitch; spaceKey = f.SpaceSwitch; spaceCycle = 0;
                if (f.Debug != debug) { SetDebug(f.Debug); Log("журнал включён"); }
                SetAutostart(f.Autostart);
                lastWnd = IntPtr.Zero;   // чтобы isRdp пересчитался с новым ignore_rdp
                SaveConfig();
                RebuildTray();
            }
        } finally { settingsOpen = false; }
    }

    static Icon MakeIcon() {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            g.FillEllipse(Brushes.Black, 1, 1, 29, 29);
            using (var p = new Pen(Color.White, 2)) g.DrawEllipse(p, 2, 2, 27, 27);
            using (var f = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel)) {
                var sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString("Яz", f, Brushes.White, new RectangleF(0, 0, 32, 31), sf);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool IsAutostart() {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Swtchr") != null;
    }
    static void SetAutostart(bool on) {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
            if (k == null) return;
            if (on) k.SetValue("Swtchr", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("Swtchr", false);
        }
    }
    // ------------------------------------------------------------ активное окно
    void UpdateWnd() {
        IntPtr fg = N.GetForegroundWindow();
        if (fg == lastWnd) return;
        lastWnd = fg; buf.Length = 0; selected = false;
        string pn = "";
        try { uint pid; N.GetWindowThreadProcessId(fg, out pid); pn = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { }
        var sb = new StringBuilder(128); N.GetClassName(fg, sb, 128); string cls = sb.ToString();
        isRdp = ignoreRdp && (pn == "mstsc" || pn == "msrdc" || pn == "msrdcw" || pn == "vmconnect");
        // в консолях Ctrl+C без выделения — прерывание, а Home/End не выделяют: там работает только путь по набранному
        isConsole = cls == "ConsoleWindowClass" || cls == "CASCADIA_HOSTING_WINDOW_CLASS" || cls == "mintty" || pn == "windowsterminal";
    }
    // ------------------------------------------------------------ хуки
    IntPtr KbHook(int code, IntPtr wp, IntPtr lp) {
        if (code >= 0) {
            var k = (N.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(N.KBDLLHOOKSTRUCT));
            if ((k.flags & N.LLKHF_INJECTED) == 0) {
                int msg = (int)wp;
                bool down = msg == N.WM_KEYDOWN || msg == N.WM_SYSKEYDOWN;
                int vk = (int)k.vkCode;
                if (!down && swallowed.Count > 0 && swallowed.Remove(vk)) return (IntPtr)1;
                if (down && autoRunning && !IsModifier(vk) && Array.IndexOf(keyVks, vk) < 0
                    && !N.Down(N.VK_CONTROL) && !N.Down(N.VK_MENU) && !N.Down(N.VK_LWIN) && !N.Down(N.VK_RWIN)) {
                    lock (pending) {
                        if (autoRunning) {
                            pending.Enqueue(new[] { vk, (int)k.scanCode, N.Down(N.VK_SHIFT) ? 1 : 0 });
                            swallowed.Add(vk);
                            return (IntPtr)1;
                        }
                    }
                }
                if (Array.IndexOf(keyVks, vk) >= 0) {
                    // состояние триггера ведём всегда, даже во время busy — иначе пропустим отпускание
                    if (down) {
                        if (!held.Contains(vk)) {   // автоповтор пропускаем
                            // отпускание могло потеряться (Win+L, UAC) — сверяемся с реальным состоянием
                            held.RemoveWhere(v => !N.Down(v));
                            if (held.Count == 0) dirty = false;
                            held.Add(vk);
                            if (!keyDown && ComboDown()) {
                                keyDown = true;
                                armed = !dirty && !OtherModifierDown();   // Alt+Shift при триггере Shift и т.п. — не наше
                            }
                        }
                    } else {
                        held.Remove(vk);
                        if (held.Count == 0) dirty = false;
                        if (keyDown) {
                            keyDown = false;
                            if (armed) {
                                bool later;
                                lock (runLock) { later = busy; if (later) queuedPress = true; }   // занято — выполним следом
                                if (!later) { UpdateWnd(); if (!isRdp) Run(DoWord); }
                            }
                            armed = false;
                        }
                    }
                } else if (down) {
                    armed = false; inputGen++;
                    if (held.Count > 0 && !IsLockKey(vk)) dirty = true;
                    if (!busy) {
                        UpdateWnd();
                        if (!isRdp) { NoteSelectionKey(vk); Track(vk, k.scanCode, N.Down(N.VK_SHIFT), true); }
                    } else Log("занято — нажатие не учтено, vk=" + vk);
                }
            }
        }
        return N.CallNextHookEx(kbHook, code, wp, lp);
    }

    bool OtherModifierDown() {
        foreach (int vk in ModVks) if (Array.IndexOf(keyVks, vk) < 0 && N.Down(vk)) return true;
        return false;
    }

    static bool IsLockKey(int vk) { return vk == N.VK_CAPITAL || vk == N.VK_NUMLOCK || vk == N.VK_SCROLL; }

    IntPtr MsHook(int code, IntPtr wp, IntPtr lp) {
        if (code >= 0 && !busy) {
            var m = (N.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(N.MSLLHOOKSTRUCT));
            if ((m.flags & N.LLMHF_INJECTED) == 0) {
                int msg = (int)wp;
                if (msg == N.WM_LBUTTONDOWN) {
                    buf.Length = 0; armed = false; inputGen++; if (held.Count > 0) dirty = true;
                    bool near = Math.Abs(m.pt.x - mDownPt.x) <= 4 && Math.Abs(m.pt.y - mDownPt.y) <= 4;
                    clickCount = near && m.time - mDownTime <= (uint)SystemInformation.DoubleClickTime ? clickCount + 1 : 1;
                    mDownPt = m.pt; mDownTime = m.time;
                } else if (msg == N.WM_LBUTTONUP) {
                    bool dragged = Math.Abs(m.pt.x - mDownPt.x) > 4 || Math.Abs(m.pt.y - mDownPt.y) > 4;
                    selected = dragged || clickCount >= 2;
                } else if (msg == N.WM_RBUTTONDOWN || msg == N.WM_MBUTTONDOWN || msg == N.WM_XBUTTONDOWN) {
                    buf.Length = 0; armed = false; inputGen++; if (held.Count > 0) dirty = true;
                }
            }
        }
        return N.CallNextHookEx(msHook, code, wp, lp);
    }

    static bool IsModifier(int vk) {
        switch (vk) {
            case N.VK_SHIFT: case N.VK_LSHIFT: case N.VK_RSHIFT: case N.VK_CONTROL: case N.VK_LCONTROL: case N.VK_RCONTROL:
            case N.VK_MENU: case N.VK_LMENU: case N.VK_RMENU: case N.VK_LWIN: case N.VK_RWIN:
            case N.VK_CAPITAL: case N.VK_NUMLOCK: case N.VK_SCROLL:
                return true;
        }
        return false;
    }

    static bool IsNav(int vk) {
        return vk == N.VK_LEFT || vk == N.VK_RIGHT || vk == N.VK_UP || vk == N.VK_DOWN
            || vk == N.VK_HOME || vk == N.VK_END || vk == N.VK_PRIOR || vk == N.VK_NEXT;
    }

    void NoteSelectionKey(int vk) {
        if (IsModifier(vk)) return;
        bool ctrl = N.Down(N.VK_CONTROL), shift = N.Down(N.VK_SHIFT);
        if (shift && IsNav(vk)) selected = true;
        else if (ctrl && vk == 'A') selected = true;
        else selected = false;
    }

    void Track(int vk, uint sc, bool shift, bool auto) {
        if (IsModifier(vk)) return;
        if (N.Down(N.VK_CONTROL) || N.Down(N.VK_MENU) || N.Down(N.VK_LWIN) || N.Down(N.VK_RWIN)) { buf.Length = 0; return; }
        switch (vk) {
            case N.VK_BACK: if (buf.Length > 0) buf.Length--; return;
            case N.VK_RETURN: case N.VK_TAB: case N.VK_ESCAPE: case N.VK_INSERT: case N.VK_DELETE: case N.VK_APPS:
                buf.Length = 0; return;
        }
        if (IsNav(vk)) { buf.Length = 0; return; }
        string s = VkToText(vk, sc, lastWnd, shift);
        if (s == null) return;
        if (auto && spaceKey && s == " " && SpaceCycle()) return;
        if (buf.Length == 0) doneAt = -1;
        buf.Append(s); if (buf.Length > 4000) { buf.Remove(0, buf.Length - 2000); doneAt = -1; }
        if (auto) CheckAuto(s == " ");
    }

    static string VkToText(int vk, uint sc, IntPtr fg, bool shift) {
        var st = new byte[256];
        if (shift) { st[N.VK_SHIFT] = 0x80; st[N.VK_LSHIFT] = 0x80; }
        if ((N.GetKeyState(N.VK_CAPITAL) & 1) != 0) st[N.VK_CAPITAL] = 1;
        uint pid; IntPtr hkl = N.GetKeyboardLayout(N.GetWindowThreadProcessId(fg, out pid));
        var sb = new StringBuilder(8);
        int r = N.ToUnicodeEx((uint)vk, sc, st, sb, 8, 4 /* не трогать состояние клавиатуры */, hkl);
        if (r <= 0) return null;
        string s = sb.ToString(0, r);
        foreach (char c in s) if (char.IsControl(c)) return null;
        return s;
    }
    // ------------------------------------------------------------ действия (в отдельном STA-потоке,
    // чтобы не держать поток хука: Windows снимает хук, который долго не отвечает)
    readonly object runLock = new object();
    bool queuedPress;                   // клавишу-триггер нажали, пока шло действие
    void Run(Action a) {
        busy = true;
        var t = new Thread(delegate () {
            try { a(); } catch { }
            while (true) {
                lock (runLock) {
                    if (!queuedPress) { busy = false; break; }
                    queuedPress = false;
                }
                try { DoWord(); } catch { }
            }
        });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
    }

    // ------------------------------------------------------------ автопереключение
    // вызывается из хука после каждого символа (он уже в buf, но до окна ещё не дошёл);
    // atSpace — это был пробел: слово закончено
    void CheckAuto(bool atSpace) {
        if (!autoSwitch || isConsole) return;
        string s = buf.ToString();
        int end = atSpace ? s.Length - 1 : s.Length, start = end;
        while (start > 0 && s[start - 1] != ' ') start--;
        if (start == doneAt) { if (atSpace) doneAt = -1; return; }
        if (end - start < 2) return;
        string word = s.Substring(start, end - start);
        string conv = AutoConvert(word, atSpace);
        if (conv == null) { if (atSpace) Log("авто «" + word + "» — не трогаем"); return; }
        Log("авто «" + word + "» → «" + conv + "»" + (atSpace ? " (пробел)" : " (на ходу)"));
        if (!atSpace) doneAt = start;   // дальше слово набирается уже в нужной раскладке
        autoRunning = true;
        Run(delegate { DoAuto(start, word, conv, atSpace); });
    }

    // слово в другой раскладке, если его надо перебить; иначе null
    string AutoConvert(string word, bool full) {
        int from = Layout.Direction(word);   // 1 — набрано латиницей, 2 — кириллицей
        if (from == 0) return null;
        int letters = 0, upper = 0; bool innerUpper = false;
        for (int i = 0; i < word.Length; i++) {
            char c = word[i];
            if (from == 1 ? Layout.IsRu(c) : Layout.IsEn(c)) return null;     // смесь алфавитов — не наше
            if (!Layout.IsEn(c) && !Layout.IsRu(c) && !Layout.Maps(c, from)) return null;   // цифры, @, / ...
            if (char.IsLetter(c)) {
                if (char.IsUpper(c)) { upper++; if (letters > 0) innerUpper = true; }
                letters++;
            }
        }
        bool caps = upper == letters;
        if (innerUpper && !caps) return null;          // iPhone, getValue — код и имена
        if (caps && letters <= 3) return null;         // SQL, API, КБ — аббревиатуры
        string lw = word.ToLowerInvariant();
        foreach (string ig in ignoreWords) if (lw.StartsWith(ig)) return null;

        string conv = Layout.Convert(word);
        int to = 3 - from;
        string cc = Norm(Core(conv)), oc = Norm(LettersOf(word));
        foreach (char c in cc) if (!(to == 1 ? Layout.IsEn(c) : Layout.IsRu(c))) return null;
        if (cc.Length < 2) return null;
        if (full && (from == 1 ? Ngram.ShortRu : Ngram.ShortEn).Contains(lw)) return conv;
        // само слово — из списка частых своего языка (щас, the): набрано верно
        if (full && (from == 1 ? Ngram.ShortEn : Ngram.ShortRu).Contains(conv.ToLowerInvariant())) return null;
        if (cc.Length < 3 || oc.Length < 2) return null;
        // чем меньше букв и чем раньше (на ходу), тем строже порог
        double t = full ? (cc.Length == 3 ? 3.0 : 1.5) : (cc.Length == 3 ? 5.0 : cc.Length == 4 ? 3.5 : 2.5);
        Ngram mf = from == 1 ? Ngram.En : Ngram.Ru, mt = to == 1 ? Ngram.En : Ngram.Ru;
        double d = mt.Score(cc, full) - mf.Score(oc, full);
        return d > t ? conv : null;
    }

    static string Norm(string w) { return w.ToLowerInvariant().Replace('ё', 'е'); }
    static string LettersOf(string w) {
        var sb = new StringBuilder(w.Length);
        foreach (char c in w) if (char.IsLetter(c)) sb.Append(c);
        return sb.ToString();
    }

    // слово без знаков по краям
    static string Core(string w) {
        int a = 0, b = w.Length;
        while (a < b && !char.IsLetter(w[a])) a++;
        while (b > a && !char.IsLetter(w[b - 1])) b--;
        return w.Substring(a, b - a);
    }

    void DoAuto(int start, string word, string conv, bool full) {
        try {
            Retype(buf.Length - start, conv + (full ? " " : ""), switchLayout ? Layout.Direction(word) : 0, false);
            buf.Length = start; buf.Append(conv); if (full) buf.Append(' ');
            lastAutoBuf = buf.ToString(); lastAutoWord = word.ToLowerInvariant(); lastAutoFull = full;
        } finally { ReplayPending(); }
    }

    // ------------------------------------------------------------ триггер «пробел»
    // вызывается из хука на пробел, до того как он попал в buf; true — пробел наш (окно его получит,
    // но Retype сотрёт его вместе с остальным)
    bool SpaceCycle() {
        string cur = buf.ToString();
        if (spaceCycle > 0 && cur != cycleBuf) spaceCycle = 0;
        if (spaceCycle == 0) {
            // «слово␣» + пробел
            if (cur.Length < 2 || cur[cur.Length - 1] != ' ' || cur[cur.Length - 2] == ' ' || cur == cycleEndBuf) return false;
            string body = cur.Substring(0, cur.Length - 1);
            int start = body.LastIndexOf(' ') + 1;
            string word = body.Substring(start);
            if (Layout.Direction(word) == 0) return false;
            // сразу после автоперебивки — это отмена: слово в исключения
            if (lastAutoBuf != null && cur == lastAutoBuf) { AddIgnore(lastAutoWord); Log("отмена автоперебивки «" + lastAutoWord + "» — в исключения"); }
            lastAutoBuf = null;
            cycleOrig = body;
            spaceCycle = start == 0 ? 2 : 1;   // строка из одного слова — шаг «вся строка» пропускаем
            string conv = Layout.Convert(word);
            SpaceStep(cur.Length - start + 1, conv + " ", Layout.Direction(word), body.Substring(0, start) + conv + " ");
        } else if (spaceCycle == 1) {
            spaceCycle = 2;
            string all = Layout.Convert(cycleOrig) + " ";
            SpaceStep(cur.Length + 1, all, Layout.Direction(cycleOrig), all);
        } else {
            spaceCycle = 0;
            cycleEndBuf = cycleOrig + " ";
            SpaceStep(cur.Length + 1, cycleEndBuf, Layout.Direction(Layout.Convert(cycleOrig)), cycleEndBuf);
        }
        return true;
    }

    void SpaceStep(int backspaces, string text, int dir, string newBuf) {
        cycleBuf = newBuf;
        autoRunning = true;
        Run(delegate {
            try {
                Retype(backspaces, text, switchLayout ? dir : 0, false);
                buf.Length = 0; buf.Append(newBuf);
            } finally { ReplayPending(); }
        });
    }

    // нажатое во время автоперебивки — повторить уже в новой раскладке
    void ReplayPending() {
        while (true) {
            int[] k;
            lock (pending) {
                if (pending.Count == 0) { autoRunning = false; break; }
                k = pending.Dequeue();
            }
            if (N.Down(N.VK_SHIFT) && k[2] == 0) ReleaseModifiers();
            var list = new List<N.INPUT>();
            if (k[2] != 0) list.Add(Key(N.VK_LSHIFT, false));
            list.Add(Key(k[0], false)); list.Add(Key(k[0], true));
            if (k[2] != 0) list.Add(Key(N.VK_LSHIFT, true));
            Send(list);
            Thread.Sleep(5);
            Track(k[0], (uint)k[1], k[2] != 0, false);
        }
    }

    // короткое нажатие: выделение, иначе последнее слово
    void DoWord() {
        long t0 = clock.ElapsedMilliseconds;
        WaitModifiersUp();
        Log("нажатие: выделение=" + selected + ", набрано=" + buf.Length + ", шаг=" + keyStep
            + (keyStep > 0 && keyGen != inputGen ? " (был ввод — сначала)" : "") + ", ждали отпускания " + (clock.ElapsedMilliseconds - t0) + " мс");
        // сразу после автоперебивки — это отмена: слово запоминаем, чтобы больше не трогать
        if (lastAutoBuf != null && !selected) {
            string cur = buf.ToString();
            bool same = lastAutoFull ? cur == lastAutoBuf
                : cur.StartsWith(lastAutoBuf) && cur.IndexOf(' ', lastAutoBuf.Length) < 0;   // то же слово ещё набирается
            if (same) {
                AddIgnore(lastAutoWord);
                if (!lastAutoFull) { int st = lastAutoBuf.LastIndexOf(' ') + 1; doneAt = st; }
                Log("отмена автоперебивки «" + lastAutoWord + "» — в исключения");
            }
        }
        lastAutoBuf = null;
        if (selected) { selected = false; keyStep = 0; if (!isConsole && ConvertSelection(true) != null) return; }
        if (keyStep > 0 && keyGen != inputGen) keyStep = 0;
        string s = buf.ToString();
        if (s.Length > 0 && (keyStep == 0 || keyOrig != null)) {
            if (keyStep == 0) {
                int end = s.Length; while (end > 0 && s[end - 1] == ' ') end--;
                int start = end; while (start > 0 && s[start - 1] != ' ') start--;
                if (start == end) return;
                string word = s.Substring(start, end - start), tail = s.Substring(end);
                string conv = Layout.Convert(word);
                Retype(s.Length - start, conv + tail, switchLayout ? Layout.Direction(word) : 0);
                buf.Length = start; buf.Append(conv).Append(tail);
                keyOrig = s; selOrig = null;
                keyStep = start == 0 ? 2 : 1;   // строка из одного слова — шаг «вся строка» пропускаем
            } else if (keyStep == 1) {
                string all = Layout.Convert(keyOrig);
                Retype(s.Length, all, switchLayout ? Layout.Direction(keyOrig) : 0);
                buf.Length = 0; buf.Append(all);
                keyStep = 2;
            } else {
                Retype(s.Length, keyOrig, switchLayout ? Layout.Direction(Layout.Convert(keyOrig)) : 0);
                buf.Length = 0; buf.Append(keyOrig);
                keyStep = 0;
            }
            keyGen = inputGen;
            return;
        }
        // ничего не набрано (после клика/навигации) — через выделение
        if (isConsole) return;
        if (keyStep == 0 || selOrig == null) {
            // слово перед курсором через Ctrl+Shift+Left
            Chord2(N.VK_CONTROL, N.VK_SHIFT, N.VK_LEFT);
            Thread.Sleep(40);
            string w = ConvertSelection();
            if (w == null) { keyStep = 0; return; }
            selOrig = w; selConv = Layout.Convert(w); keyOrig = null;
            keyStep = 1;
        } else if (keyStep == 1) {
            // вся строка; перебитое на первом шаге слово оставляем как есть
            Tap(N.VK_HOME); Chord(N.VK_SHIFT, N.VK_END);
            Thread.Sleep(60);
            string line = ConvertSelection(false, delegate (string l) {
                int k = l.LastIndexOf(selConv);
                if (k < 0) return Layout.Convert(l);
                return Layout.Convert(l.Substring(0, k)) + selConv + Layout.Convert(l.Substring(k + selConv.Length));
            });
            if (line == null) { keyStep = 0; return; }
            int at = line.LastIndexOf(selConv);
            selLine = at < 0 ? line : line.Substring(0, at) + selOrig + line.Substring(at + selConv.Length);
            keyStep = 2;
        } else {
            Tap(N.VK_HOME); Chord(N.VK_SHIFT, N.VK_END);
            Thread.Sleep(60);
            string orig = selLine;
            ConvertSelection(false, delegate (string l) { return orig; });
            keyStep = 0;
        }
        keyGen = inputGen;
    }

    // keepSel: выделение было пользовательским — после вставки выделить вставленное снова,
    // чтобы повторное нажатие перебило его обратно
    // Возвращает скопированный (исходный) текст; null — ничего не выделено.
    string ConvertSelection(bool keepSel = false, Func<string, string> transform = null) {
        ReleaseModifiers();
        string text = CopySelection();
        if (string.IsNullOrEmpty(text)) { Log("выделение: ничего не скопировалось (" + copyMs + " мс)"); return null; }
        string conv = transform != null ? transform(text) : Layout.Convert(text);
        long t0 = clock.ElapsedMilliseconds;
        SetClipText(conv);
        Thread.Sleep(30);
        Chord(N.VK_CONTROL, 'V');
        // Shift+Left встают в очередь ввода за Ctrl+V — окно выполнит их уже после вставки
        if (keepSel && Reselect(conv)) selected = true;
        buf.Length = 0;
        if (switchLayout) SwitchLayout(Layout.Direction(text));
        Log("выделение: копирование " + copyMs + " мс, вставка+раскладка " + (clock.ElapsedMilliseconds - t0) + " мс, " + text.Length + " симв.");
        RestoreClipLater();
        return text;
    }

    // Shift+Left по числу шагов курсора во вставленном тексте (перевод строки \r\n — один шаг, суррогатная пара — один)
    static bool Reselect(string text) {
        int n = 0;
        for (int i = 0; i < text.Length; i++) {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            if (char.IsLowSurrogate(text[i])) continue;
            n++;
        }
        if (n == 0 || n > 5000) return false;
        var list = new List<N.INPUT> { Key(N.VK_LSHIFT, false) };
        for (int i = 0; i < n; i++) { list.Add(Key(N.VK_LEFT, false)); list.Add(Key(N.VK_LEFT, true)); }
        list.Add(Key(N.VK_LSHIFT, true));
        Send(list);
        return true;
    }

    // ------------------------------------------------------------ ввод
    static N.INPUT Key(int vk, bool up) {
        var i = new N.INPUT(); i.type = 1; i.u.ki.wVk = (ushort)vk;
        i.u.ki.dwFlags = (up ? N.KEYEVENTF_KEYUP : 0) | (vk >= 0x21 && vk <= 0x2E ? N.KEYEVENTF_EXTENDEDKEY : 0);
        return i;
    }
    static N.INPUT Uni(char c, bool up) {
        var i = new N.INPUT(); i.type = 1; i.u.ki.wScan = c;
        i.u.ki.dwFlags = N.KEYEVENTF_UNICODE | (up ? N.KEYEVENTF_KEYUP : 0);
        return i;
    }
    static void Send(List<N.INPUT> list) {
        if (list.Count == 0) return;
        N.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(N.INPUT)));
    }
    static void Tap(int vk) { Send(new List<N.INPUT> { Key(vk, false), Key(vk, true) }); }
    static void Chord(int mod, int vk) { Send(new List<N.INPUT> { Key(mod, false), Key(vk, false), Key(vk, true), Key(mod, true) }); }
    static void Chord2(int m1, int m2, int vk) {
        Send(new List<N.INPUT> { Key(m1, false), Key(m2, false), Key(vk, false), Key(vk, true), Key(m2, true), Key(m1, true) });
    }

    static readonly int[] ModVks = { N.VK_LSHIFT, N.VK_RSHIFT, N.VK_LCONTROL, N.VK_RCONTROL, N.VK_LMENU, N.VK_RMENU, N.VK_LWIN, N.VK_RWIN };

    // подождать, пока пользователь физически отпустит модификаторы (до 1,5 с), иначе отпустить их принудительно
    static void WaitModifiersUp() {
        for (int i = 0; i < 60; i++) {
            bool any = false;
            foreach (int vk in ModVks) if (N.Down(vk)) { any = true; break; }
            if (!any) return;
            Thread.Sleep(25);
        }
        ReleaseModifiers();
    }

    // отпустить физически зажатые модификаторы (иначе Backspace с Ctrl удалит слово, Shift оставит выделение)
    static void ReleaseModifiers() {
        var list = new List<N.INPUT>();
        foreach (int vk in ModVks) if (N.Down(vk)) list.Add(Key(vk, true));
        Send(list);
        if (list.Count > 0) Thread.Sleep(20);
    }

    // Стереть backspaces символов и набрать text. Как dotSwitcher: сначала переключить раскладку,
    // потом нажать те же клавиши (VkKeyScanEx) — Unicode-ввод (VK_PACKET) часть приложений
    // и RDP превращают в мусор. Unicode остаётся запасным путём для символов, которых нет в раскладке.
    static void Retype(int backspaces, string text, int dir, bool wait = true) {
        if (wait) WaitModifiersUp(); else ReleaseModifiers();
        var list = new List<N.INPUT>();
        for (int i = 0; i < backspaces; i++) { list.Add(Key(N.VK_BACK, false)); list.Add(Key(N.VK_BACK, true)); }
        Send(list);
        Thread.Sleep(20);
        IntPtr hkl = SwitchLayout(dir);
        bool caps = (N.GetKeyState(N.VK_CAPITAL) & 1) != 0;
        list.Clear();
        foreach (char c in text) {
            short vs = hkl != IntPtr.Zero ? N.VkKeyScanEx(c, hkl) : (short)-1;
            if (vs == -1 || (vs & 0x600) != 0) {           // нет в раскладке или нужен Ctrl/Alt — Unicode
                list.Add(Uni(c, false)); list.Add(Uni(c, true));
                continue;
            }
            int vk = vs & 0xFF;
            bool shift = (vs & 0x100) != 0;
            if (caps && char.IsLetter(c)) shift = !shift;
            if (shift) list.Add(Key(N.VK_LSHIFT, false));
            list.Add(Key(vk, false)); list.Add(Key(vk, true));
            if (shift) list.Add(Key(N.VK_LSHIFT, true));
        }
        Send(list);
    }

    // Переключить раскладку активного окна; вернуть её HKL, когда окно её приняло (или IntPtr.Zero)
    static IntPtr SwitchLayout(int dir) {
        if (dir == 0) return IntPtr.Zero;
        long lang = dir == 1 ? 0x0419 : 0x0409;
        int n = N.GetKeyboardLayoutList(0, null);
        if (n <= 0) return IntPtr.Zero;
        var arr = new IntPtr[n]; N.GetKeyboardLayoutList(n, arr);
        IntPtr target = IntPtr.Zero;
        foreach (IntPtr h in arr) if (((long)h & 0xFFFF) == lang) { target = h; break; }
        if (target == IntPtr.Zero) return IntPtr.Zero;
        IntPtr fg = N.GetForegroundWindow();
        uint pid; uint tid = N.GetWindowThreadProcessId(fg, out pid);
        if (N.GetKeyboardLayout(tid) == target) return target;
        N.PostMessage(fg, N.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, target);
        for (int i = 0; i < 20; i++) {
            Thread.Sleep(25);
            if (N.GetKeyboardLayout(tid) == target) { Thread.Sleep(30); return target; }
        }
        return IntPtr.Zero;   // окно раскладку не приняло — печатаем Unicode
    }

    // ------------------------------------------------------------ буфер обмена.
    // Таймауты с запасом: через RDP буфер обмена ходит с заметной задержкой.
    static IntPtr clipWnd;
    readonly object clipLock = new object();
    bool restorePending; string restoreText; int restoreGen;

    // приложение читает буфер обмена не мгновенно (через RDP — тем более): вернуть прежний текст через 300 мс,
    // если за это время не началось новое копирование
    void RestoreClipLater() {
        if (savedClip == null) return;
        int g;
        lock (clipLock) { restorePending = true; restoreText = savedClip; g = ++restoreGen; }
        var t = new Thread(delegate () {
            Thread.Sleep(300);
            lock (clipLock) {
                if (!restorePending || g != restoreGen) return;
                restorePending = false;
                SetClipText(restoreText);
            }
        });
        t.IsBackground = true; t.Start();
    }
    string savedClip;                   // текст, бывший в буфере до копирования; null — там был не текст (не трогаем)

    // копирует выделение; null — ничего не скопировалось (буфер обмена восстановлен)
    long copyMs;
    string CopySelection() {
        long t0 = clock.ElapsedMilliseconds;
        try { return CopySelectionCore(); } finally { copyMs = clock.ElapsedMilliseconds - t0; }
    }
    // буфер не очищаем: ждём, пока после Ctrl+C сменится его номер версии
    string CopySelectionCore() {
        // прошлое действие ещё не вернуло буфер — исходное содержимое берём у него, а не из буфера
        bool fromPending;
        lock (clipLock) { fromPending = restorePending; savedClip = fromPending ? restoreText : GetClip(); restorePending = false; restoreGen++; }
        uint seq = N.GetClipboardSequenceNumber();
        Chord(N.VK_CONTROL, 'C');
        for (int i = 0; i < 60 && N.GetClipboardSequenceNumber() == seq; i++) Thread.Sleep(10);
        if (N.GetClipboardSequenceNumber() == seq) {   // ничего не выделено — буфер не тронут
            if (fromPending) SetClipText(savedClip);   // но возврат прошлого действия отменили — вернуть сейчас
            return null;
        }
        Thread.Sleep(20);   // некоторые приложения кладут несколько форматов подряд
        string text = GetClip();
        if (string.IsNullOrEmpty(text)) { if (savedClip != null) SetClipText(savedClip); return null; }
        return text;
    }

    static bool OpenClip() {
        for (int i = 0; i < 50; i++) { if (N.OpenClipboard(clipWnd)) return true; Thread.Sleep(10); }
        return false;
    }
    // текст из буфера; null — там не текст или буфер занят
    static string GetClip() {
        if (!OpenClip()) return null;
        try {
            if (!N.IsClipboardFormatAvailable(N.CF_UNICODETEXT)) return null;
            IntPtr h = N.GetClipboardData(N.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr ptr = N.GlobalLock(h);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); } finally { N.GlobalUnlock(h); }
        } finally { N.CloseClipboard(); }
    }
    static void SetClipText(string s) {
        if (!OpenClip()) return;
        try {
            N.EmptyClipboard();
            if (string.IsNullOrEmpty(s)) return;
            IntPtr h = N.GlobalAlloc(N.GMEM_MOVEABLE, (UIntPtr)((s.Length + 1) * 2));
            if (h == IntPtr.Zero) return;
            IntPtr ptr = N.GlobalLock(h);
            if (ptr == IntPtr.Zero) { N.GlobalFree(h); return; }
            Marshal.Copy(s.ToCharArray(), 0, ptr, s.Length);
            Marshal.WriteInt16(ptr, s.Length * 2, 0);
            N.GlobalUnlock(h);
            if (N.SetClipboardData(N.CF_UNICODETEXT, h) == IntPtr.Zero) N.GlobalFree(h);   // иначе память принадлежит системе
        } finally { N.CloseClipboard(); }
    }
}
// ---------------------------------------------------------------- окно настроек
class SettingsForm : Form {
    static readonly string[] KeyNames = { "LShift", "RShift", "Shift", "LCtrl", "RCtrl", "Ctrl", "LAlt", "RAlt", "Alt", "LWin", "RWin", "Win",
        "Ctrl+Shift", "LCtrl+LShift", "RCtrl+RShift", "Alt+Shift", "Ctrl+Alt" };
    readonly ComboBox key = new ComboBox();
    readonly CheckBox cbLayout = new CheckBox(), cbRdp = new CheckBox(), cbAuto = new CheckBox(), cbAutoSw = new CheckBox(), cbSpace = new CheckBox(), cbDebug = new CheckBox();

    public string Key { get { return key.Text; } }
    public bool SwitchLayout { get { return cbLayout.Checked; } }
    public bool IgnoreRdp { get { return cbRdp.Checked; } }
    public bool AutoSwitch { get { return cbAutoSw.Checked; } }
    public bool SpaceSwitch { get { return cbSpace.Checked; } }
    public bool Debug { get { return cbDebug.Checked; } }
    public bool Autostart { get { return cbAuto.Checked; } }

    public SettingsForm(string keyName, bool switchLayout, bool ignoreRdp, bool autoSwitch, bool spaceSwitch, bool autostart, bool debug, Icon icon) {
        Text = "Swtchr — настройки"; Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; TopMost = true;
        AutoScaleMode = AutoScaleMode.Font; Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(400, 288);

        int y = 14;
        Add(new Label { Text = "Клавиша или сочетание через «+» (нажать и отпустить):", AutoSize = true, Location = new Point(14, y) });
        y += 24;
        key.DropDownStyle = ComboBoxStyle.DropDown;   // можно вписать своё сочетание
        key.Items.AddRange(KeyNames);
        key.Location = new Point(14, y); key.Width = 160;
        Add(key);
        int ki = -1;
        for (int i = 0; i < KeyNames.Length; i++) if (KeyNames[i].Equals(keyName.Trim(), StringComparison.OrdinalIgnoreCase)) ki = i;
        if (ki >= 0) key.SelectedIndex = ki; else key.Text = keyName.Trim();
        Add(new Label { Text = "выделение / слово → строка → обратно", AutoSize = true, Location = new Point(186, y + 4) });

        y += 40;
        cbLayout.Text = "Переключать раскладку после конверсии"; cbLayout.Checked = switchLayout;
        cbLayout.AutoSize = true; cbLayout.Location = new Point(14, y); Add(cbLayout);
        y += 26;
        cbRdp.Text = "Не работать в окне RDP-клиента (там свой Swtchr)"; cbRdp.Checked = ignoreRdp;
        cbRdp.AutoSize = true; cbRdp.Location = new Point(14, y); Add(cbRdp);
        y += 26;
        cbAutoSw.Text = "Автопереключение (слово не в той раскладке — само)"; cbAutoSw.Checked = autoSwitch;
        cbAutoSw.AutoSize = true; cbAutoSw.Location = new Point(14, y); Add(cbAutoSw);
        y += 26;
        cbSpace.Text = "Пробел ещё раз после слова: слово → строка → обратно"; cbSpace.Checked = spaceSwitch;
        cbSpace.AutoSize = true; cbSpace.Location = new Point(14, y); Add(cbSpace);
        // только один из двух режимов
        cbAutoSw.CheckedChanged += delegate { if (cbAutoSw.Checked) cbSpace.Checked = false; };
        cbSpace.CheckedChanged += delegate { if (cbSpace.Checked) cbAutoSw.Checked = false; };
        if (cbAutoSw.Checked && cbSpace.Checked) cbSpace.Checked = false;
        y += 26;
        cbAuto.Text = "Запускать вместе с Windows"; cbAuto.Checked = autostart;
        cbAuto.AutoSize = true; cbAuto.Location = new Point(14, y); Add(cbAuto);
        y += 26;
        cbDebug.Text = "Вести журнал (Swtchr.log, для отладки)"; cbDebug.Checked = debug;
        cbDebug.AutoSize = true; cbDebug.Location = new Point(14, y); Add(cbDebug);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(206, 248), Size = new Size(84, 28) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(298, 248), Size = new Size(84, 28) };
        Add(ok); Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }

    void Add(Control c) { Controls.Add(c); }
}

// ---------------------------------------------------------------- таблицы троек букв
// Веса = Σ(1 + ln частоты слова) по словам из строк интерфейса Windows (System32\ru-RU|en-US\*.mui), ё → е.
static class NgramData {
    public const string Ru =
        "^аа7 ^аб4b ^ав1c3 ^аг39 ^ад113 ^аз31 ^аи2 ^ай11 ^ак103 ^алa8 ^ам32 ^ан14a ^ап68 ^ар158 ^ас7e ^ат83 ^ау6e ^аф21 ^ах7 ^ач7 ^аш3 ^аэ2 ^ба12f ^бд2 " +
        "^бе182 ^бз5 ^биe7 ^бк2 ^блf0 ^бнd ^боab ^бп2 ^бр9b ^бс3 ^бт5 ^буef ^бх6 ^бщa ^бъ7 ^бы81 ^бэ4 ^бюe ^ваd2 ^ввa1 ^вд8 ^ве1f4 ^вз68 ^ви1c3 " +
        "^вкf5 ^влa6 ^вм1f ^вн10b ^во3ac ^вп1e ^вр8a ^вс153 ^вт45 ^ву8 ^вхa6 ^вч6 ^вы6d2 ^вьd ^га93 ^гбc ^гв1b ^ггa ^гд9 ^ге5f ^ги89 ^глac ^гнf ^го13f " +
        "^гр16b ^гу1e ^гц4 ^гъ2 ^гэ5 ^да152 ^дб7 ^двc1 ^дд6 ^де289 ^дж31 ^дз6 ^ди26f ^дл7c ^дн29 ^до4cb ^дрbe ^дс2 ^дт5 ^ду65 ^дх3 ^дыb ^дь2 ^дэ3 " +
        "^дю12 ^ев18 ^ег14 ^ед60 ^ее8 ^еж29 ^ез3 ^ей8 ^ек10 ^ел13 ^ем1d ^ен13 ^ер3f ^ес20 ^ет1b ^еч4 ^еш2 ^ещ8 ^ею2 ^жа18 ^жб5 ^жд23 ^же89 ^жи3a " +
        "^жк8 ^жн5 ^жо2 ^жу40 ^заd93 ^зб5 ^зв95 ^зд1c ^зе5e ^зи13 ^зк2 ^зл11 ^змd ^знd2 ^зо40 ^зр27 ^зу10 ^зы7 ^иа6 ^иб5 ^ивb ^иг70 ^ид9f ^ие1d " +
        "^иж4 ^из2be ^ии3 ^ий2 ^икb ^ил10 ^им16f ^ин36c ^иоa ^ип9 ^ир1e ^ис3ea ^ит37 ^иф3 ^их8 ^ич9 ^ищ9 ^июe ^ия3 ^йа5 ^йв2 ^йд2 ^йе7 ^йи3 " +
        "^йк5 ^йл7 ^йн2 ^йоa ^йсc ^йт8 ^ка34a ^кб10 ^кв71 ^кг5 ^кд3 ^ке19 ^ки91 ^кл209 ^км3 ^кн4d ^ко884 ^кп3 ^кр1d0 ^ксc ^кт8 ^куa2 ^кх15 ^кш3 " +
        "^кы4 ^кэ7b ^кю3 ^ла72 ^лв4 ^леca ^лж2 ^ли185 ^лнc ^лоfc ^лс3 ^лу55 ^лч3 ^лы6 ^ль33 ^лэ2 ^лю61 ^ляb ^ма369 ^мбf ^мг17 ^ме292 ^ми126 ^мк5 " +
        "^млa ^ммd ^мнf6 ^мо24d ^мп6 ^мр2 ^мсa ^му8b ^мч2 ^мы1d ^мь12 ^мэ9 ^мю5 ^мя1a ^на703 ^нв3 ^нг7 ^нд6 ^неb3c ^нж2 ^ни10a ^нк4 ^нн3 ^но159 " +
        "^нр4 ^нс4 ^нт6 ^ну95 ^нч3 ^ны3 ^нь10 ^ню8 ^ня3 ^оа2 ^об6ec ^ов18 ^огaf ^од14f ^ое2 ^ож8a ^оз37 ^ои3 ^ой5 ^окe1 ^ол33 ^ом18 ^он24 ^оо2 " +
        "^оп324 ^ор97 ^ос26f ^от8ec ^оф1e ^ох1a ^оц39 ^оч7f ^ош62 ^ощ4 ^оэ2 ^па283 ^пб6 ^пв5 ^пе7bf ^пз3 ^пиa1 ^пк9 ^пл154 ^пн3 ^по133b ^пр14c7 ^пс21 ^пт8 " +
        "^пу126 ^пф2 ^пх3 ^пш2 ^пы25 ^пь7 ^пя1a ^ра91a ^рв3 ^ре437 ^ри5e ^роcc ^рп2 ^рс3 ^рт9 ^ру87 ^рх2 ^ры12 ^рэ3 ^ря12 ^са151 ^сбd4 ^св228 ^сг1c " +
        "^сд4f ^се3ad ^сж4a ^сз3 ^си276 ^ск1b3 ^сл23b ^смc8 ^снa9 ^со777 ^сп1a7 ^срe4 ^сс4a ^ст42c ^су139 ^сф3a ^сх33 ^сц37 ^сч7b ^сш12 ^съ2a ^сы5 ^сь5 ^сэ5 " +
        "^сю5 ^та170 ^тбc ^тв10 ^те295 ^тиb3 ^тк14 ^тл3 ^тм6 ^тн3 ^то1b8 ^тп4 ^тр283 ^тс7 ^тт3 ^ту79 ^тх5 ^тч2 ^тш5 ^тщ3 ^ты9 ^ть4 ^тэ2 ^тюe " +
        "^тя6 ^уб26 ^ув8b ^уг4f ^уд1be ^ужc ^уз54 ^уй8 ^ук139 ^ул47 ^ум76 ^ун94 ^уо5 ^уп111 ^ур52 ^ус2eb ^ут74 ^уф2 ^ух10 ^уц2 ^учc6 ^уш4 ^уэ2 ^уя1b " +
        "^фа10e ^фе25 ^фи134 ^фл4e ^фо14f ^фр86 ^фт2 ^фу7f ^фь2 ^ха51 ^хв1c ^хе1d ^хи19 ^хл5 ^хм4 ^хо87 ^хр8e ^ху6 ^хъ2 ^хэ23 ^ца2 ^цв62 ^це13a ^ци90 " +
        "^цпb ^цс7 ^цы2 ^чаfc ^че12c ^чж2 ^чи9d ^чл35 ^чн5 ^чо7 ^чр5 ^чт32 ^чу1d ^чч5 ^чьa ^ша76 ^шв19 ^ше4d ^шиd0 ^шкe ^шл24 ^шн5 ^шоf ^шп11 " +
        "^шр26 ^шт3a ^шуc ^шэ2 ^ща2 ^ще22 ^щиa ^ъе5 ^ыбf ^ыв6 ^ыдd ^ые3 ^ыз7 ^ый5 ^ык6 ^ыл2 ^ып6 ^ыр3 ^ысc ^ытc ^ых6 ^ыч2 ^ыш2 ^ьб3 " +
        "^ьз6 ^ьк2 ^ьн4 ^ьт3 ^эб8 ^эв1e ^эг2 ^эд5 ^эк128 ^эл98 ^эм3c ^эн3d ^эпb ^эрb ^эс2b ^эт82 ^эф44 ^эх8 ^эш4 ^юб5 ^юг9 ^юд2 ^юж19 ^юк3 " +
        "^юл2 ^юм4 ^юн11 ^юр8 ^ют3 ^юч1b ^ющ3 ^яб2 ^яв6f ^яд22 ^яз50 ^якc ^ям7 ^ян17 ^яп1a ^яр33 ^яс3 ^ятa ^яч10 ^ящ11 аа$a аааa ааб3 аад2 " +
        "аамf аан6 аар1b аб$14 аба75 абб3 абв8 абе18 абж4 абзe аби4e аблde абн4 або1fc абр21 абс3b абу12 абы12 ав$3f ава124 авг7 авд2 аве108 ави2c7 " +
        "авк8a авл4c0 авн106 аво79 аврa авс17 авт189 аву2 авц3 авш83 авщ34 авы27 авь22 авяc аг$15 агаa7 агб2 агв8 агд4 аге2a аги3f агл22 агм4f агн47 " +
        "аго79 агр173 агс2 агуf ад$3d ада1d0 адг3 адеf4 адж12 адз4 ади75 адк5f адл39 адм69 адн24 адо52 адп4 адр117 адс35 аду12 адц2e адч1f адш6 ады1b " +
        "адь5 адэ2 ае$a аев7 аез2 аек2 аел2 аем388 аен3 ает495 аеч2 аж$8 ажаa0 ажд43 ажеc1 ажи2f ажк11 ажмc ажн28 ажо7 ажу6 аз$2e аза1a6 азб77 " +
        "азв67 азг25 аздc6 азе29 ази40 азк12 азл41 азмaf азн125 азо12d азр17b азс4 азу40 азц16 азъ15 азыa2 азя3 аи$f аиб16 аив7c аид2 аик2 аил16 аим5b " +
        "аинe аир2 аитd аич3 аия2 ай$27 айа4 айб3 айв50 айд5f айе9 айз14 айи2 айк19 айлb2 айм38 айн55 айо7 айп6 айр8 айс2d айт148 айш17 айя6 " +
        "ак$38 ака7a акв9 акд5 аке7c акж7 аки58 аккf акл5d акм4 ако18d акр115 аксa4 акт237 аку26 акц40 ал$126 ала195 албb алв3 алг1c алд9 але14d алж7 " +
        "али32f алкe алл43 алм4 алн4 ало1ad алс54 алт15 алу1e алф15 алы34 аль5a6 алю8 аля37 ам$1ea ама33 амб1c амв2 аме16d амз2 ами32e амк17 амл6 аммc8 " +
        "амн9 амо96 амп28 амс24 амуc амх3 амч5 амы2e амя23 ан$209 ана2b8 анб2 анв2 анг80 анд1a1 ане133 анжb анз34 аниa53 анк38 анм2 анн8ca ано412 анрa " +
        "анс1cd ант106 ану1f анц4d анч19 анш19 аны107 ань1e аняb9 ао$9 аоб6 аол2 аом2 аор4 аосa аот2 ап$13 апа60 апе49 апи103 апк42 апл53 апнb апо9f " +
        "апп4d апр24f апсd апт50 апу12a апы3 апь3 апю3 апя16 ар$3b ара1a1 арбe арв5 арг2e ард10 аре132 ари12a арк5e арм13 арн41 ароa0 арпe арс2a арт125 " +
        "аруdb арф2 архdd арш7a ары2d арьf арэ2 арю5 аря42 ас$3c аса3a асе1f аси5d аск7a асл4d асм2 аснdf асо9b асп1c7 аср4 асс152 аст3a7 асу11 асх19 " +
        "асц2 асч10 асшee асы3a ась75 ат$a3 ата1a9 атвb ате3cf ати2d9 атк61 атл15 атм4 атнed ато288 атрdc атс1f атт1c ату75 атф3f атх3 атч37 атыc9 ать778 " +
        "атю2 ау$19 ауд60 ауз28 аук6 аул5 аун9 аур7 аусf аут2a ауш9 ауэ27 аф$a афа1e афг7 афе6 афиd5 афл2 афо14 афр1b афт2 афф9 ах$242 аха16 " +
        "ахв18 ахе7 ахи8 ахл2 ахмf ахо68 ахр6 ахс8 аху3 ац$6 аца5 ацеe аци4d4 ацк7 ач$16 ача12c аче15a ачиc2 ачк39 ачнa3 ачо9 ачу15 ачь3 аш$e " +
        "ашаb ашд2 аше72 аши10e ашк12 ашл7 ашм3 ашн2e ашо4 ашс5 ашт3 ашу6 аща68 ащеd4 ащиf1 ащу6 аэ$2 аэл5 аэр2 аю$11 аюс2 ают183 ающ3a7 ая$b99 " +
        "аяв2e аял4 аям2 аян4 аяо2 аяс3b аях5 ба$4a баб7 бавbc баг10 бадa баз57 баи3 бай69 бакd бал6b бам1a бан29 бар17 бас12 бат64 баф3 бахb " +
        "бач3 башc баю8 баяa ббо7 бвг3 бве8 бд$3 бда4 бди2 бе$17 бег11 бед16 бежd безcb беи4 бей8 бекa бел66 бем6 бен33 бер5a бесc2 бетf " +
        "бецe бже5 бзаf бзо14 би$b биб3a бивc биг3 бид2 биеd биз9 бий5 бикa бил73 бим2 бин31 био23 бир6f бис6 бит83 бичb биш2 бия17 бка2d " +
        "бкеa бки20 бко15 бкуa бл$3 бла7a бле7c бли167 бло19f блу2 бль3 блю31 бляe бм$3 бма3 бме23 бн$2 бнаa5 бнеb бни4 бно198 бну1c бны44 бо$1b " +
        "бов78 богf бод53 бое1e бож35 боз27 боиc бой41 бок28 болbd бом36 бон19 боп4 бор105 босf бот168 бох2 боч73 бою3 боя10 бп$4 бр$5 бра435 бре46 " +
        "бри32 бро9f бру8 бры5 брьb брю4 бря6 бск2b бсл42 бсо14 бст42 бсу6 бте4 бтиc бу$1a буа3 буб2 був9 буг8 буд4a буе57 буж10 буйb бук4d " +
        "бул14 бум16 бун4 бур23 бус7 бут44 буф3e бух5 бучe буэ2 бую2f буя2 бха2 бхи2 бхо6a бцаb бце6 бцоa бцу3 бцы8 бше2 бща18 бще98 бщи46 " +
        "бщу5 бъеcf бъя68 бы$29 быв1a быеe быйb был28 бым9 быс40 быт5c быхa быч3e бэй3 бэн2 бюд6 бюл5 бюс2 бя$6 бяз60 ва$140 вав39 ваг3 вад46 " +
        "вае335 важ28 ваз6 ваи12 вай37 вак7 вал1c6 вам54 ванc6e варab васa ват5ed вах1b вац3f вачf ваш4a ваю211 ваяeb вв$4 вве62 вви3 вво3b вг$3 вгд2 " +
        "вгу4 вда4 вдв3 вдл2 вдо1d ве$76 вебb вев2 вег4 вед1d0 вее9 вежd вез1f вей31 век22 вел8b вен161 вер69d весd4 вет1b8 вец6 веч3d вешa вещ67 " +
        "вза47 взв7 взи7 взл5 взрa взя5 ви$2b виа1e виб3 вив3b виг2b вид19b вие52 виж20 виз48 вии23 вий20 вик14 вил192 вим2f вин59 вир16b вис8d вит218 " +
        "вих3 виц7 вич1a виш2a виюb вия6a вк$7 вкаd6 вке54 вкиc1 вклee вко3e вкр2 вку6c вл$6 вла42 вле54a влиa9 вло48 влю4 вля266 вм$2 вмеb9 вн$4 " +
        "вна66 вне118 вни5f вно1e8 вну63 вны14c вню5 вня22 во$f1 воб7a вовb2 вогea вод1e5 вое10b вож9 воз201 вои43 вой17f вок3f волde вомf0 вон4a вооb воп2a " +
        "вор8b вос193 вот29 воу2 воч6b вош1a вою6 вп$2 впа43 впе8 впл5 впо4 впр8 вр$4 враcc вре13d ври27 вро22 вру7 вря3 вс$8 все3a вск2a всл9 " +
        "всп42 встdf всю4 вся5 вт$7 вто291 втр4 ву$3f вуа4 вуе41 вуй4 вук46 вул3 вумb вун5 вус11 вух12 вуч12 вуш7 вую18e вх$3 вхоa6 вцо2 вцу2 " +
        "вч$3 вче4 вша20 вше63 вши88 вшуc вщи7b вы$58 выбde выв96 выг43 выдe5 выеcb вызdc выи3 выйd3 вык54 выл4 вымa4 вын13 вып165 выр74 выс98 вытc " +
        "вых105 выч57 выш9b выя29 вь$f вье13 вьи2 вьт24 вью3 вья7 вэ$2 вяж8 вязdd вянe вят12 га$85 габ1e гав8 гаг5 гадb гае57 гаж2 газ1c гаи7 " +
        "гай12 гак3 гал45 гам38 ган68 гап4 гар4f гас9 гат4c гау4 гаф2 гахa гац15 гаш2 гаю22 гаяd гб$6 гба5 гбе3 гби5 гбо3 гва1d гвиc гво3 " +
        "гг$9 гга2 гггd гге17 гги2 ггц4 гда26 гдеa ге$21 геб3 гей6 гек3 гел2 гем3 ген67 гео24 гер45 гза3 ги$3d гиа2 гиб3c гив17 гиг17 гие13 " +
        "гизa гии19 гий17 гик7 гил6 гим1b гин1b гио27 гип32 гир47 гисda гитc гихb гич4d гиюc гия23 гкаa гки6 гкоe гл$2 гла12d гле15 гли2a гло5d " +
        "глу3b глы5 глю3 гля1a гма9 гме6d гмы2 гн$3 гна34 гнеc гниd гно98 гну27 го$bde гоа21 гоб7 говbb гог10 год53 гое11 гоз3 гой17 гок17 голd6 " +
        "гом31 гон31 гооd гоп2d горb2 гос90 готd7 гоу17 гофd гоц2 гош2 гоэ7 гоя10 гр$b гра29f гре50 гри1e гро67 гру240 гры33 грэ3 гря13 гс$4 гск8 " +
        "гст5 гто2 гу$2b гуа14 гудa гул43 гум26 гун9 гур63 гус8 гут12 гущ3 гую8 гфл2 гхт4 гц$10 гча4 гчеa гър2 гыз2 гыл4 гэл5 да$144 даб2 " +
        "давe0 даг4 дадf даеb4 дажa дай1d дак3b дал19b дам3b дан213 дап50 дар9f дас13 дат10c дау2 дах1d дац10 дачb0 даш7 даю9f дая7 дб$3 дби2 дбм3 " +
        "дбо4 два3d две26 дви75 дво70 двс2 дву31 дгг2 дго4b дгрe дд$7 дда2 ддг2 ддеdf ддиb ддт2 ддх8 де$7a деа10 деб5 дев13 дед1a дее8 деж50 " +
        "дез5 деи8 дей133 декc6 дел458 дем4e ден41e деоd6 деп8 дер245 дес6e дет92 деф23 дец4 деч2 деш7 дею5 дея5 дж$a джа2c дже27 джи24 джо16 джп3 " +
        "джт3 джу3 дза14 дзв2 дзе5 дзи8 дзо5 дзя2 ди$34 диаf1 див2b дигd дидd дие8 диз5 дии9 дий1b дик36 дил3d димa9 дин228 дио5e дип2 дир7a " +
        "дис120 дит21b диу6 диф27 диц26 дич22 диш3 дию3 дия20 дк$3 дка6d дке14 дки27 дкл115 дко2f дку1e дл$6 дла23 длеae длиa9 дло3e для19 дм$2 дма2a " +
        "дмеb дми67 дмн14 дму2 дн$8 дна7d дне89 дни7e дно1ee дну1f дныe2 днюc дня3b до$27 доа2 добfc дов1ac догd дод2 дое5 дож1e доз18 дои2 дой16 " +
        "док64 долbb дом132 дон62 доп158 дор3b дос289 дот24 дох1e доч6d дош9 дпа17 дпиb3 дпо68 дпр53 др$14 драc9 дреd1 држ2 дри11 дрк4 дро7d дрс2 дру6b " +
        "дры3 дря3 дс$3 дсв9 дсе13 дси20 дск77 дсо14 дсс3 дст135 дсчa дт$2 дтв58 дти6 дто2 дтч2 дтя3 ду$65 дуаe дуб4c дув3 дуг16 дуе54 дуж3 " +
        "дуй5 дук21 дул44 дум8 дун14 дуп4f дур34 дус4e дут18 душ5 дущ6a дуэ4 дую83 дфу3 дхаb дхи5 дхо34 дху3 дца31 дце5 дче1b дчи33 дша3 дше36 " +
        "дши26 дъя6 ды$6f дыв16 дыд39 дые7 дый7 дымc дын2 дыр2 дыя2 дь$f дьм5 дьт4 дью5 дья2 дэл3 дюй10 дюс4 дя$11 дям3 дян5 дяс2 дят25 " +
        "дящcc еа$2 еавb еагa еад1d еаи2 еак40 еал55 еан5d еап2 еас2 еатe еб$b еба8 ебе44 ебиa ебл34 ебн33 ебо80 ебр7 ебс2 ебу7c ебы3 ебя6 " +
        "ев$45 ева79 евд1c евеa3 евз4 еви28 евн23 ево15a евр39 евсa еву1f евш30 евы134 евьb евя9 ег$12 ега39 егг3 егд9 еге10 еги154 егкb егм20 его252 " +
        "егр35 егу50 егчe ед$30 еда117 едв67 еде394 едж7 едзd еди1f3 едкc едлb5 едмc еднe3 едо2fb едпac едр1e едсc0 еду150 едш4c едъ5 еды44 едьd едя9 " +
        "ее$1cb еев6 еей3 еек4 еем4 еерa еес4b еет17 еж$4 ежа52 еждd8 еже8c ежз3 ежи88 ежк5 ежн96 ежп2 ежс12 ежу23 ез$1b еза1aa езвc езд3b езеa8 " +
        "езж6 езз6 ези2e езк13 езлd езм4 езн4f езо8c езр2 езс4 езу5a езы5 езь4 еи$e еив3 еид2 еиз55 еим44 еин17 еис45 еих3 ей$372 ейв3 ейд1c " +
        "ейе16 ейз3 ейк1e ейл4 ейм11 ейн62 ейр2 ейс1d2 ейт29 ейцc ейчb ейш3d ек$7b ека7e екб4 екв12 еке11 еки20 екл7f екоe6 екрb4 екс185 ект1e8 екуc8 " +
        "екц3d екш8 екэ5 ел$7c елаe8 елг3 еле3a2 ели18c елк43 елл19 елм4 елн3 елоa6 елтa елу1d елх4 елч10 елы32 ель60a елю43 еля19d ем$40f ема11c емб10 " +
        "еме1f4 еми11 емк19 емл1a емнc4 емо2d1 емп31 емс14 ему108 емы3d2 емьa емя15 ен$34f ена333 енв2 енг28 ендa5 ене1c6 енз45 ениf4c енк3c енл8 еннe32 ено308 " +
        "енс78 ент471 ену40 енц51 енш4 енщ12 ены1d6 еньce енюb еняc6 ео$11 еоаe еоб108 еовc еог22 еод30 еож2a еоз1e еоиb еок2b еол4 еом14 еон6 еоп9e " +
        "еорe еосb еот1b еоу4 еоф11 еп$2 епа1a епе39 епи20 епк2 еплb2 епо102 епр118 епс2 епт3 епу22 епц4 епч5 епь3 епя1c ер$16f ера2f3 ерб26 ерв1d7 " +
        "ерг6d ерд2f ере90d ерж22e ерз2 ери1c3 еркba ерл17 ерм4c ерн24b еро137 ерп4a ерр1b ерсe9 ерт129 еру72 ерф45 ерх6a ерц1f ерч10 ершe4 ерыbe ерь2c ерю5 " +
        "еряd1 ес$2b еса52 есв7 есд2 есеab есж8 еси22 еск445 есл25 есм8 есн56 есоf2 еспaf ессa4 ест575 есу7e есц6 есч6 есъc есы17 есь96 есяc7 ет$574 " +
        "ета12e етб5 етв6b ете18d етиb8 етк7f етл25 етн107 етоff етр109 етс3c4 етт8 ету1c етч63 еты52 еть6f етя1b еув3 еуг14 еуд3c еук9 еул2 еум5 еун4 " +
        "еуп1a еус4a еут2 еф$4 ефа7 ефе5 ефи2a ефн4 ефо56 ефр14 ех$20 еха1b ехб5 ехв38 ехе2 ехи7 ехк3 ехл6 ехм10 ехн28 ехо34 ехр2 ехс2 ехт2 " +
        "ец$35 еце4 еци7b ецк17 ецу2 ецы4 еч$4 ечаef ече116 ечиb2 ечк15 ечнba ечу7 ечь16 еш$c еша6c еше9f еши43 ешк4 ешл5 ешн76 ешс6 ешт5 ешь2 " +
        "ещаc5 еще130 еэк2 ею$9 еюн6 еютe еющ46 ея$22 еявc еям4 еян2 еяс3 еят5 еящ2 жа$17 жаб4 жав8 жае87 жаз3 жай21 жак2 жал33 жам2 жан46 " +
        "жарc жас5 жатf3 жащ73 жаю22 жая7 жб$8 жба1c жбе9 жбо7 жбуb жбыc жд$4 жда6b жде16b ждиe ждо25 жду3a жды1b же$3b жеб1c жев11 жед12 жез2 " +
        "жей4 жекc жел3e жем12 жен407 жер13 жесa6 жет3b жзи2 жи$12 жиб4 жив15f жидbe жие6 жизb жикd жил27 жим90 жинc жир2f жис8 житba жих7 жицe " +
        "жия5 жк$3 жка30 жке10 жки22 жко1b жку12 жмд2 жме3 жми9 жн$2 жна38 жне31 жни2d жноee жну11 жныa4 жню3 жня8 жоб2 жойc жокe жол2 жом3 " +
        "жон2 жор6 жоу2 жоч3 жпр2 жпу3 жса5 жск10 жсо2 жст2 жта3 жу$8 жун2 жур41 жут27 жущ3 жчи4 жь$4 жьт2 жэн2 за$5a заа18 заб6e зав170 " +
        "заг17d зад159 зае5 заж6 заз3 заи49 зайf зак1cf зал48 замf3 зан1cb зап475 зарd0 зас3e зат1aa заф21 зах2b зац144 зачd заш59 защaa зая2e зб$2 зба2 " +
        "збе1a зби22 збл46 збо10 збр1b збу5 збы18 зв$5 зва7f зве152 зви25 звл4f звн3 зво105 звр9c зву57 звы3 зги2 згл6 зго26 згр18 зд$e зда11f здв3 " +
        "здеdd зди3 здн27 здо15 здр6 зду6 зды8 зе$1b зей6 зел2a зем2f зен1a зерff зетb зец6 зжа6 зже7 ззв3 ззн3 зи$16 зивc зиг7 зид11 зие4 " +
        "зииe зийc зик2 зил1f зим16 зин32 зиоe зир139 зит7b зиц24 зич34 зию5 зия2c зка45 зке1a зки4b зко53 зку23 зл$3 зла19 злеa зли47 зло25 злу8 " +
        "злы5 зм$b змаa зме1ca змо8f зму2 змыe зн$3 зна2db зне1c зни68 зно60 зну7 зны46 зо$7 зоб87 зов30b зог7 зод2 зой12 зок15 зол2d зом14 зон88 " +
        "зоп85 зор5d зос4 зот5 зоу19 зох3 зоч2e зош1e зп$2 зр$5 зра78 зре106 зри1b зро8 зру4 зры1a зря4e зсе2 зск25 зст4 зу$3c зуа15 зубc зуе8b " +
        "зуй14 зул4f зум11 зунb зусb зуч8 зую4f зуя6 зц$2 зцаb зцо6 зцы3 зчи12 зши3 зъе13 зъя8 зы$22 зыв10f зый2 зык7b зыл2 зымa зырc зыч7 " +
        "зь$b зьк2 зьт4 зью3 зья5 зя$9 зяиc зям3 зят7 иа$12 иаг52 иад2 иак9 иал154 иам4 иан43 иап42 иар2 иат38 иафc иацf иб$9 ибаd ибе19 " +
        "иби11 ибк53 ибл4d ибо4c ибп3 ибр32 ибс6 ибу3d ибш2 ибы2 ив$67 ива507 иве97 иви119 ивк1a ивл7 ивн1f3 иво77 ивр17 ивс6 иву10 ивш54 ивы3b ивя40 " +
        "иг$12 ига61 игб3 игг17 игд3 иге10 игз3 иги21 игл2c игм6 игн7e иго32 игрa6 игу60 игх4 ид$1a идаe2 идд8 иде1fc идж14 иди46 идк3 идн2c идо8 " +
        "идсc идт3 иду1c иды8 идя4 ие$a0c иевf иег2 иез4 ией92 ием234 иенa1 иер1b иес54 иет3 иж$2 ижа2a ижд3 иже61 ижи12 ижн42 ижу7 из$26 иза178 " +
        "изб45 изв1ba изг1e изд28 изе7 изи168 изк56 изл6 изм139 изн44 изо16d изр5 изсc изу47 изш3 изъ5 изы4 изь4 ии$5a7 иик2 иир51 ий$7cf ийк3 ийн3c " +
        "ийсe1 ик$131 ика3b2 ике57 икиa3 икл51 икн1a ико13b икр5b иксaa икт62 ику76 икш19 ил$100 ила115 иле8f или15c илк3 илл29 ило111 илс33 илу1c илх2 илы8 " +
        "иль1b6 илю9 иля26 им$21a има1a4 имб9 имв50 име24d ими169 имк1a имм23 имн3b имо19d имп62 имс23 иму4a имф2 имы12b имя23 ин$74 ина222 инб7 инв20 инг55 " +
        "индd3 ине153 инж4 инз2 ини262 инк2f инл7 инн92 ино90 инсbc инт196 ину9c инф57 инхb7 инц7 инч2 ины4d инь19 иня7b ио$16 иоа3 иоб1b иов3 иог3 " +
        "иод3c иозa иок9 иол7 иом31 ион16b иоп19 иор28 иос5e иот3c иоуc ип$1b ипа33 ипе41 ипи13 иплc ипо28 ипп10 ипрe ипс9 ипт63 ипу10 ипф3 ипыb " +
        "ир$16 ира93 ирг10 ире8b ири63 иркc ирлb ирмb ирн3c иро987 ирп4 ирсf ирт5a иру2ce ирх2 иры5 ирю4 иря28 ис$3a иса135 исб2 исв31 исе37 иси7d " +
        "иск1f1 исл104 исн36 исо92 исп2bc исс18 ист3bc ису4e исх8e исч37 исы76 исьbe ися3b ит$18f ита152 итв4 ите6cc итиb3 итк1c итм1f итн5b итоb0 итр34 итс9b " +
        "итт4 иту36 иты50 ить51f итя9 иуг2 иуе2 иул2 иусa иуэ3 иф$5 ифа4 ифе26 ифи152 ифмd ифн4 ифо5 ифр126 ифт22 иффf ифц2 ифы4 их$27e иха5 " +
        "ихе4 ихи7 ихк3 ихо25 ихп2 ихс42 ихт5 иху4 иц$1f ица96 ицб3 ице86 ици177 ицкa ицо8 ицу26 ицы2c ич$3 ича2b иче493 ичиaf ичк3 ичн1c3 ичт1e " +
        "ичу6 ичь8 иш$a иша7 ишеd иши14 ишк9 ишл5 ишн1b ишп11 ишуb ишь4 ищ$5 ища32 ище83 ищи3 ищу5 ию$2e0 июл6 июн7 ия$aa8 ияв5 ияд2 ияе9 " +
        "иял2 иям126 иян17 ияс2 ият1d иях92 ияюc йа$3 йан3 йба2 йв$2 йваb йве47 йгу6 йд$4 йде65 йдж1a йди17 йдо4 йду4 йды2 йдя6 йек2 йем5 " +
        "йен8 йер17 йза2 йзе10 йзи6 йи$3 йиг3 йид3 йк$3 йка49 йке17 йки26 йкл3 йко16 йкрa йку12 йкь2 йл$15 йла2b йлеa йли3 йло4c йлс3 йлу7 " +
        "йлы14 йля6 йм$d йма25 йме1a ймо9 ймп3 ймы6 йн$1f йна1f йнд2 йне42 йни4 йно67 йну8 йны4f йо$3 йол2 йон2 йор4 йотa йп$2 йпа3 йпо2 " +
        "йра6 йре5 йро3 йс$9 йса16 йсб3 йсе7 йскf3 йсн4 йсоa йст1e6 йсу5 йсы6 йся71 йт$27 йта31 йте153 йти3f йтнf йто2a йтрb йтс2 йту5 йтх6 " +
        "йтыf йцаb йчаc йчи29 йша5 йше29 йши22 йшу4 йю$3 йя$6 йях2 ка$594 кааa каб2b кав18 кад3f кае62 каж51 каз231 каи7 кай20 как5f кал18b кам11c " +
        "кан165 као9 кап20 карa4 кас2b кат1cc кау2 каф8 ках6e кацa2 кач9c каш7 каю4d кая22d кб$7 кба3 кби6 кбо4 кв$9 ква79 кве16 кви1e кво3b кву5 " +
        "квы6 кг$2 кго2 кгц4 кд$3 кдо6 ке$1f7 кеа12 кедa келe кем5 кен27 кер37 кес5 кет9d кеч4 кея2 кже7 кзе25 ки$44e кив31 киг3 кид6 кие11a " +
        "кизf кий338 кик4 кил19 ким9f кин25 кио3 кипa кир16b кис16 кит24 кихba кич3 кия4 кка7 кке3 кко8 кку9 кл$26 кла189 кле24 клиc7 клм2 кло8e " +
        "клт4 клу6 клы5 клю439 км$3 кма9 кме9 кмо3 кна14 кне9 кни21 кно58 кнр5 кну3b кня3 ко$8c коб19 ков2f5 когda кодdb кое14d кож2 коз8 кои6 " +
        "кой1c8 кок18 колf9 ком3ca кон438 коо24 копf9 кор1dc кос9e кот97 коу13 коф4 коч7 кош3 коэc коя3 кпе2 кпо2 кр$c кра1be креfd криd6 кро96 круa0 " +
        "кры1ef крюa кс$3e кса3f ксе33 ксз2 кси125 кск25 ксн14 ксо35 ксп70 кстb8 ксу1a ксф2 ксч2 ксы11 кт$4c кта66 кте41 кти1c4 ктн62 кто102 ктр3f ктуc7 " +
        "кты26 ктя5 ку$253 куа7 куб18 кув4 кудa куеf куз2 куй5 кук8 кул15 кум4b кун4f куп20 кур64 кус34 кут10 кучb куш2 кущ4d кую4d кхаb кхм7 " +
        "кхо3 кху3 кце5 кци10b кша3 кшеd кши11 кшу2 кыз2 кыр2 кья2 кэ$2 кэй5 кэр3 кэш7e кюр3 ла$2a9 лаб18 лавa1 лаг95 лад118 лае2c лаж28 лаз16 " +
        "лаи2 лай27 лак20 лал15 лам9d лан15d лаоa лап6 лар12 лас209 латdb лауc лах30 лацc лач27 лаш27 лаю11 лая18 лб$2 лбаd лбе8 лби2 лбц1e лва2 " +
        "лвс3 лгаd лге2 лги4 лго36 лгр2 лд$3 лда5 лде5 лди2 лдо5 ле$c7 леа2 леб2 левcd лег71 лед17e лее28 леж97 лез33 леи2 лейb5 лекe0 лел1e " +
        "лем113 лен9ac лео7 леп10 лер4d лес38 лет9f леу3 леф4c лецe леч3f леш2 лею3 леяd лжа1c лже2a лжи28 лжн1d лзо2 лзуb ли$230 лиаd либ2a ливc8 " +
        "лиг11 лидf лие71 лиж24 лиз19f лии7 лий21 лик12a лил30 лим39 линf5 лио3c лип2a лир10c лисc8 лит180 лиф21 лих5 лицea лич181 лиш1d лищ27 лия54 лк$9 " +
        "лка30 лкеf лки35 лкл3 лкн15 лко1b лку13 лл$8 лла12 лле83 лли42 лло11 лля2 лма5 лмн2 лмы2 лн$6 лна14 лнеd3 лниe6 лно80 лну9 лнц4 лны30 " +
        "лняc5 ло$15a лоб45 лов1df лог131 лодb лое17 лож1f4 лоз4 лои6 лой1f лок1eb лом77 лонdf лоо2 лоп6 лорd лос100 лот53 лоуd лоф5 лох16 лоч39 лошf " +
        "лощ7 лоя9 лпа2 лпо3 лс$3 лсе4 лск2 лстe лся99 лт$7 лтаc лти9 лто9 лты8 лу$59 луа4 луб2f луг15 луд4 лужd6 луй9 лукa лул3 лумe " +
        "лунb луо9 луп15 лур4 лус6 лутc лух14 луч183 луш5e луэ3 лую3 лф$2 лфа13 лхе2 лхи4 лч$2 лча19 лчкc лчо6 лщи9 лы$84 лыб8 лыв1c лые11 " +
        "лыж3 лый19 лык19 лым12 лыхb лыш6 ль$18f льб23 львc льг1c льд1c лье5 льж4 льз22b льк49 льм1a льнab4 льс105 льт143 льфa льц41 льч9 льш7d лью16 " +
        "лья13 лэш5 лю$71 люб3a люд3e люз28 люкc люс17 лют1b люч425 люш8 лющ4 ля$11c ляв3 лядb ляе19f ляй12 лял1f лям4e лян18 ляр4c ляс4 лятd9 лях23 " +
        "ляц55 ляю121 ляя17 ма$126 маб2 мавb маг48 мадe мае3a маз9 май31 мак89 мал155 мам3a ман104 мао6 мап4 марe5 мас94 мат1af мау3a мафe мах29 мац42 " +
        "мач2 маш5a маю1e маяb3 мб$b мба19 мбд4 мбе2 мби2d мбл6 мбоb мбу1d мва2 мве2 мво4f мгн11 мго4 мгц4 мд$2 мдд3 ме$6d меа2 меб2 мев9 " +
        "мег16 мед95 мее13 меж70 мей27 мек9 мел31 мен739 мер1b1 мес170 мет1ed меу2 мех1f мецd меч60 меш38 мещ111 мею4f мея2 мза2 ми$72d миб6 миг1e мид6 " +
        "миз49 мии6 мик69 мил41 мимc мин168 мир6c мис1d мит70 мих2 мич51 мию2 мия8 мка1e мке6 мки10 мкн11 мко2a мкс4 мку3 мкэ2 мла9 мле50 мли6 " +
        "мля12 мм$22 мма40 ммд3 мме4c мми19 ммм4 ммн32 ммоa мму31 ммы22 мн$4 мна22 мне1d мни29 мно1c0 мнуf мны9e мню2 мня4 мо$5f моа8 моб3b мов3f " +
        "мог19e модc3 моеdf можb7 моз1a моиe мойe8 мок16 мол42 момd5 монec моо2 мопa мор43 мос95 мот98 моу15 моф3 мох3 моч28 мош6 мощ35 мою3 моя4 " +
        "мп$e мпа31 мпе26 мпи20 мпл37 мпоb5 мпрc мпт2 мпуe мпы4 мпь51 мро2 мру4 мс$8 мсе4 мск34 мст5 мся33 мт$2 му$35a муд7 мужa муз28 мук3 " +
        "мул77 мум1f мун9 мурa мус17 мут23 мух3 мущ6 мую6a мфл3 мфо3 мха3 мча8 мче3 мчи3 мы$b9 мые131 мый16b мык15 мыл4 мымde мынb мысc мыт8 " +
        "мых157 мыц2 мыш1a мь$4 мьи3 мьт5 мьюa мьяb мэй2 мэн7 мэш2 мю$4 мя$30 мягe мяд2 мяи2 мяк3 мял2 мян12 мяп2 мяс3 мят23 мяф6 мяч6 " +
        "мяэ3 на$4ff наб74 навd9 наг25 над11d нае2a наж52 назeb наи3f най4f накe5 нал22f намd1 нанa2 нао4 нап11d нар137 нас1b1 нат78 нау12 нах7a нац3a нач271 " +
        "наш17 наю29 ная5dc нбе2 нбо2 нбу5 нв$2 нва17 нве44 нви6 нг$3e нга4d нгб4 нгв6 нгг2 нгд2 нге17 нги8 нгк5 нгл1c нгм2 нго52 нгрa нгс8 " +
        "нгт2 нгу6 нгф2 нгы4 нд$39 ндаe6 ндв5 нде99 нджd ндз5 нди6a ндк2 ндл13 ндм25 ндн23 ндо3c ндрe ндс31 нду61 ндх6 нды1d не$d0 неа3d неб4a " +
        "нев16a нег62 нед19a нее87 неж36 незca неиaf нейb9 нек60 нел5f нем97 нен47b нео165 неп202 нер173 нес1cf нет78 неу87 неф8 нех16 нец15 неч90 неш34 неэ2 " +
        "нея14 нж$2 нжа3 нжеa нжм2 нжу2 нза31 нзи45 нзо5 ни$9b ниа7 ниб4 нив7e ниг21 нид13 ние8a2 ниж80 низ137 нии294 ний1ed ник1fc нил96 ним1d0 нин11 " +
        "нио4 нип10 нирf9 нис86 нит248 ниу5 ниф22 них42 ниц152 нич127 нию1a0 ния7dd нк$a нка69 нкеf нки2a нкл2 нкн2 нко32 нкр2f нкс2 нкт2f нку13 нкц6b " +
        "нлаd нля6 нмаc нн$4 нна214 нне7f нни63 нно99f нну126 нныc0c нню8 ння1b но$6ea ноа21 нобb нов5b3 ног722 нод5 ное57c нож5c ноз30 нои2 ной5bb нок49 " +
        "нол15 ном517 нон3d ноо6 ноп5a норbf нос441 нот27 ноу11 ноф7 нох2 ноц5 ноч25 нош3d ноэ13 нояc нпе2 нр$c нраb нро2 нры3 нс$1f нса1f нсв2 " +
        "нсе11 нси2e нск18b нсл38 нсо7c нсп3a нстdd нсуa нсф5 нсы7 нсь5 нт$90 нта1f9 нтд2 нте1fd нти170 нтн4b нто99 нтр139 нтс3e нту36 нтх2 нты4a нть2 " +
        "нтя5 ну$7c нуа6 нуб3 нув3 нуд2f нуе2 нуж5f нуй3 нук8 нул76 нум11 нун6 нуп2 нур4 нус15 нут161 нуэ3 ную2be нфе12 нфи62 нфл3c нфо3a нфр1e " +
        "нх$2 нхрb4 нц$7 нца9 нце34 нци87 нцо4 нцу1a нч$3 нча2b нче18 нчи43 нше19 ншт4 нщи11 ны$3ec ные676 ныи2 ный889 ным580 ных64f ныя2 нь$4c ньа2 " +
        "ньг4 ньеf ньи4 ньй4 ньк1f ньн2 ньо7 ньс7 ньтb ньч2 ньш6b ньюe нья16 ню$1c нюй2 нюн4 нюс2 нюю25 ня$36 няв4 няеfc няж3 няйe нял42 " +
        "нямb нят13a нях7 няш2 нящ24 няю75 няя57 оа$4 оав2 оад44 оак2 оал3 оам5 оан19 оап3 оар9 оаф5 оаэ2 об$1a оба118 обв4 обе96 обз11 оби60 " +
        "обк17 облe1 обм26 обн261 обо118 обр34f обс7a обт2 обу48 обх67 общf1 объ11b обы8c обя60 ов$54c оваfba ове2b5 ови17a овк1df овл22e овм9c овнf1 ово39d овп43 " +
        "оврab овс17 овтcc ову5c овх4 овщ46 овы283 овь10 овя5 ог$27 ога70 огд16 огеb оги9d оглc4 огн1d огоb20 огр1c1 огс2 огу20 од$92 одаd7 одб6 одв2b " +
        "одг56 оддeb оде192 оджc одзc оди2d6 одк165 одл55 одм19 одн2ca одо111 одпd5 одр90 одсd3 одт5b одуa8 одф3 одх34 одч30 оды25 одэ3 одю2 одяf3 ое$8e5 " +
        "оев1c оег8 оедbb оез3 оеи2 оей8 оек1e оем16 оенd1 оет1c оец2 ож$6 ожа27 ожд91 оже216 ожи11a ожк23 ожнbe ожу2 ожь7 оз$c оза70 озб2 озв115 " +
        "озд11c озе8 озж7 ози53 озк5 озл2 озм86 озн161 озо5a озр5e озу8 озы6 озяc ои$1a оиг45 оид6 оиз118 оил7 оим17 оинc оис58 оит4b оих9 оич28 " +
        "ой$ab7 ойд22 ойк7f ойм4 ойн48 ойр6 ойс9c ойт35 ойч2a ок$197 ока1a0 окв4 окг2 оке72 оки173 оккa окл9 окн2a око1be окр84 окс10 окт15 оку88 ол$4d " +
        "ола84 олб2a олв2 олг29 олдb олеa3 олж84 олзd оли12f олк18 олл60 олн341 оло1f9 олп4 олсd олт6 олу140 олч1a олш2 олщ9 олы18 оль3fb олю22 оля74 " +
        "ом$8b9 ома14e омб2a оме23b оми98 омк28 омл34 омм3f омн7d омоdc омп11a омсa ому274 омч3 омы19 омь5 омя7 он$cc она159 онв33 онг40 онд19 оне14f они1b6 " +
        "онк64 онл3 онн1d5 оно149 онп2 онр9 онсaa онт1b6 ону34 онфaf онц2d онч6a оны2b онь9 оня39 оо$4 ооб9d оок12 ооп3 оор23 оот9e ооц2 ооч3 оощ2 " +
        "оп$1e опаcc опеcf опи170 опк30 опл21 опо213 опп2 опр1c4 опс9 опт6f опу1b9 опц4 опы65 опь2 опя3 ор$161 ора1e7 орб5 орв38 орг39 орд3b ореe8 орж2 " +
        "орзf ори203 орк33 орм1ba орнee оро2b5 орп43 орр62 орс2f орт1ab ору88 орф13 орх2 орц17 орчb орш2 орщ15 оры9f орь2 оряbd ос$5f оса4e осб11 осв6d " +
        "осе78 осж2 осиff оск51 осл111 осм57 осн103 осоe9 осп91 оср3a оссb9 остb7d осу3d осх18 осч5 осш2 осъ3 осы28 осьab осэ2 ося60 от$56 ота117 отб3d " +
        "отв15d отд4e отеe6 отз1f оти65 отк2cf отлb8 отм80 отнa6 ото343 отпda отр177 отс110 отт19 оту19 отф3b отх4 отч61 оты29 оть2 отя8 оу$b оуг19 оул5 " +
        "оум17 оуп2 оур2b оус2b оутc оф$8 офа18 офе9 офи54 офо38 офр3 офтb офу8 ох$4 оха9 охв9 охиc охл5 охо51 охрec оц$5 оцв3 оцеfa оци25 " +
        "оч$5 оча10 оче10a очи107 очк9b очн271 очт6b очу8 очьa ош$2 оша10 ошеf4 оши82 ошк3 ошл46 ошнb ошо4 ошт3 оща7 още19 ощи6 ощн21 ощр3 ощу4 " +
        "ощьf оэк10 оэл2 оэн5 оэт8 оэф13 ою$d оюз3 оя$18 ояб5 ояв2b ояд4 оязf оял4 оям8 оян75 ояр4 ояс18 оят35 оях3 оящ20 па$4d пав3 пад6a " +
        "пае9 паз32 пакad пал50 пам30 пан71 пап55 пар172 пасe6 пат8 пау12 пах16 пач2 паю10 пая2 пб$4 пво2 пвт2 пе$1f педd пей19 пек15 пелd пен40 " +
        "пер8a0 пес23 пет53 пех14 пец6e печe1 пеш49 пзу3 пи$12 пиа2 пив7 пид3 пие5 пизb пииd пий7 пик32 пил44 пин25 пио1e пир9e пис2cf пит6f пиц4 " +
        "пичa пишe пию7 пия1e пк$7 пка37 пкеd пки19 пкн2 пко12 пкс4 пку15 пл$3 пла142 плеc6 пли8f пло65 плу4 плы1d плюf пля32 пн$4 пна1a пне4 " +
        "пно5e пну6 пны5a по$17 поб7 пов228 погe под68a пое3 пожf поз14c пои32 пойd покa2 пол888 пом171 понdb поо5 поп8e пор247 пос313 пот162 пох16 поц3 " +
        "почae пошa поэ8 поя40 пп$a ппа5c ппеc ппи21 ппо2f ппу9 ппыb пр$12 пра47e пре848 при635 прм2 проaaa прс4 пру8 прыb пря5f пс$6 пса5 псе1b " +
        "псиd пскa псу9 пт$5 пта4 пте3d пти81 пто60 птс6 пты2 пу$22 пуа5 пуб6b пуг6 пуе2 пуз9 пул48 пун39 пур16 пус2b2 пут46 пуч7 пушa пущ8e " +
        "пуэ5 пфа3 пфе2 пхе2 пц$2 пци7 пча3 пче3 пше2 пы$20 пыл2 пыр2 пыт8c пышb пь$6 пье9 пью4f пья3 пюк3 пят53 пящ12 ра$2ed раб201 рав4c2 " +
        "раг7c рад81 рае36 раж117 раз5b5 раи6b рай68 рак85 ралc8 рам1e6 ран5fa рао2 рап3 рар15 рас426 рат30e рау1c рафce рах5b рац13b рач4d раш92 ращ108 раюe " +
        "рая20 рбаf рбиa рбл8 рбо5 рбс9 рбуb рбы2 рв$5 рва88 рве81 рви88 рвн37 рво2a рву3 рвы23 рг$16 рга48 ргеb рги1e ргл4 ргнe рго37 ргс5 " +
        "ргу26 ргы2 рд$15 рда11 рде7 рдж6 рди3d рдо8 рдс6 рду3 рдц8 ре$148 реа88 реб138 рев129 рег15f ред79c рее30 реж13a рез229 реи45 рей5c рек1eb рел59 " +
        "рем16a рен362 реоa1 реп121 рер92 рес1f4 рет137 реу3b реф27 рех78 рец11 речb1 реш106 рещ58 рею9 рея12 ржа9a ржд62 рже8 ржиf4 ржк3d рзиf рзь2 ри$6d " +
        "риа66 риб5c рив12f риг7b рид28 рие79 рижd ризb3 рии3a рий6c рик8e рил6a рим127 рин18c риоc2 рип77 рир122 рис15d рит1b5 риу3 риф49 рих1f риц36 ричdf " +
        "риш8 рию12 рия98 рк$b рка63 рке33 рки5d ркк2 ркл8 ркм9 ркн11 рко31 рксd ркт8 рку20 рла19 рле2 рли7 рлы1a рм$b рма131 рме31 рми60 рмлf " +
        "рмн8 рмо13 рмс2 рму1a рмы11 рмя7 рн$b рнаd8 рне69 рни4b рно10d рнс7 рну9b рныfe рню7 рня7 ро$5d роа1a робfa ровdd0 рог10b род15c роеf4 рож4c " +
        "роз57 рои1b5 рой131 рокfe ролf0 ром186 рон18c ропe4 рорa рос2a9 ротc7 роуd роф61 рох31 роцba рочb3 рошd5 рощ17 роэ3 роя23 рпа1a рпе7 рпи6 рпо40 " +
        "рпр21 рпу19 рра11 рре5e рриc рроb рс$f рса42 рсе9 рси95 рск7c рсн9 рсо49 рсп3 рсс9 рст21 рсу6 рсш2 рсы7 рт$4f ртаa6 ртв4 рте3b рти154 " +
        "ртк9 ртн94 рто69 ртрa ртс2 рту86 ртф2 рты3c рть5 ру$103 руа8 руб22 ругab руд31 руе18c руж156 руз118 руи3 руй47 рукaa рулe рум38 рун17 руп99 " +
        "рус62 рут63 руч28 руш42 руэ2 руюe8 руя6 рфа2 рфе3d рфо17 рх$17 рхж3 рхиdb рхм2 рхн34 рхо2 рхп3 рхт4 рху14 рхш3 рцаe рцеc рци1b рцо2 " +
        "рцы3 рча2 рче15 рчж2 рчи5 рша41 рше71 рши5d ршо2 ршр4f рщи15 ры$180 рыб7 рыв132 рые1a рыжd рый25 рык5 рыл11 рым1d рын3 рыт12e рых16 рыч3 " +
        "рыш9 рыя6 рь$2c рье15 рьм4 рьтb рья3 рэй2 рэн2 рэп2 рэя2 рю$a рюз4 рюк3 рюс2 рюч6 ря$26 ряв2 рядce ряе74 ряж25 ряз13 ряй4 рял11 " +
        "рям4f рян42 рят2e ряч12 рящ4 ряю57 са$dd саа12 саб9 савf сад7 сае3 саж4 саи4 сай3d сак8 сал46 самf1 сан130 сао3 сап4 сар11 сас3 сат38 " +
        "сауf сах30 сац30 сая5 сб$4 сба7 сбе14 сби3 сбо81 сбр49 св$4 сва22 свеc3 сви22 свк2 сво130 свы2 свяaa сгеb сги8 сгл4 сго2 сгр4 сд$2 " +
        "сдв12 сде3a сду4 се$63 сеа49 себc сев3e сег37 сед1d сез3 сеи3 сей2f секe2 сел41 сем6b сенa7 сеп2 сер165 сес11 сетbf сеу2 сехa сеч35 сещ1e " +
        "сея2 сжа4c сжи7 сза3 сзн2 си$54 сиа4 сиб7 сив82 сиг39 сид1a сиеd сиз2 сии1d сий13 сик1e сил8a сим11d син126 сиоb сипd сирd3 сисca ситcb " +
        "сиф1f сих3 сич42 сиш2 сиюc сия39 ск$65 ска412 скв12 ске44 ски53a склa3 скнe ско44a скрc5 скс3 ску5e сл$8 сла76 сле23c сли53 сло11e слу15f слыc " +
        "сля41 см$8 сма37 сме80 сми5 смоa2 смт2 смы5 смэ2 сн$6 сна71 сне1e сни7a сно167 сну20 сныc6 сня2a со$19 соа2 соб13c сов2a0 сог9f содac соеbd " +
        "сож5 созe7 сои4 сой5 сокa7 сол49 сом75 сон3b соо111 соп9c сорc3 сосbb сот31 соу5 софe сохc2 соц20 соч23 сош2 сою3 сп$8 спа33 спе177 спи72 " +
        "спл5f спо408 спр1ea спу19 спы12 спя12 ср$9 сра3b среa5 срк3 сро44 сс$20 сса33 ссв2 ссе22 ссиb9 сск22 ссл6 ссм1b ссн3 ссо90 сср3 сстaf ссуa " +
        "ссчf ссы6c ст$98 ста954 ств605 сте23c сти5b1 стк7f стл3 стн19d сто410 стп2 стр6d6 стс6 сту199 стх3 сты72 сть20e стю6 стя3f су$4e суаe суб39 судe " +
        "суеc суж8 сук3 сул10 сум25 сун28 суп28 сур4d сут85 суф17 сух2 сущb5 суэ4 суюc сфе13 сфо2f сфр2 сх$4 схв2 схе2c схи2 схоbe сцв4 сце3b " +
        "сча2 сче7d счи72 сш$5 сша6 сше5 сшиe0 сшт1e съе39 сы$62 сыв95 сыл8e сын2 сыр2 сыщd сь$23a сьб6 сье7 ськ5 сьм2b сьо2 сьт4 сьюf сья2 " +
        "сэк5 сэм2 сю$4 сюд6 ся$a87 сяг4 сямf сят2f сях6 сяц1d сячf сящ2b та$332 таа2 таб81 тав30d таг15 тад24 тае8e тажc таи11 тай77 так103 тал16d " +
        "тамc4 тан4f1 тап1e тар11b тас15 тат1a1 тау7 таф8 тах7c тац94 тач2 таш6 тащ8 таэ4 таю4f тая5c тб$5 тби6 тбоb тбр38 тбуe тв$39 тваbe тве2c0 " +
        "тви154 твл24 тво18a твр1e тву187 твы3 твь6 тдаa тде46 те$55c теа6 тев6f тег72 тед2 теж10 тез6 тей88 тек19c тел7bb тем128 тенfa теп22 тер3a7 тесe3 " +
        "тет33 теф4 тех2d теч34 тзв2 тзо2 тзы1b ти$2ec тиа2 тиб8 тив281 тиг3b тид6 тие66 тиж17 тиз4a тии27 тий25 тик10f тилb9 тим1cd тин68 тиоa тип72 " +
        "тир222 тис34 тит174 тиу6 тифcc тих20 тиц7 тич1ca тиш16 тиюc тия64 тк$3 тка126 тке32 ткиa8 ткл116 тко78 ткрda тку47 ткэ4 тла53 тлб2 тле11 тли4e " +
        "тло37 тлы9 тлю2 тля2 тм$b тма7 тме85 тмо8 тму2 тмы5 тн$2 тнаbf тне39 тни44 тно2bf тну27 тны22b то$d6 тоа6 тобab тов327 тогb0 тод33 тое37 " +
        "тож22 тоз43 тои14 тойb7 ток144 тол98 том1ee тонbe тоо15 топ67 тор59a тос40 тот42 тоу2 тофf тоц3 точ197 тош8 тощ2 тояb1 тпе1a тпрc2 тпу3 тр$65 " +
        "тра4f6 тре2a2 три234 тро402 труe3 тры4f тряb тс$8 тсвc тсе7 тск8e тсл3d тсо37 тсрb тст14f тсу55 тсчb тсыa тсю3 тся59a тт$7 ттаb тте28 тти6 " +
        "тто3 тту2 ту$ef туа90 тув4 туг10 тудf туе8 тук2 тулf тум9 тун3e туп153 тур101 тус15 тут6 туф2 тух6 туч4 туш4 тущ2 тую34 тфе2 тфи1a " +
        "тфо61 тха8 тхиc тхо6 тч$2 тча6 тче60 тчи92 тчк2 тчч2 тше2 тшу2 тща3 ты$1c4 тывb2 тые3b тый64 тык16 тыл3 тым40 тыр1a тыс8 тых3b тыч2 " +
        "тыш6 ть$e15 тье18 тьи8 тьс24b тьт7 тью72 тья9 тэр2 тю$2 тюж2 тюм5 тюнc тюр5 тя$e тяб9 тяж7 тям2e тян17 тят5 тях1a тящa уа$18 уаб2 " +
        "уалa9 уам4 уан13 уар7 уат8 уах6 уац18 уб$8 уба14 убб6 убд3 убе19 уби26 убкb ублbc убн7 убо1e убр4 убс2 убтb убц6 убъ19 убы14 ув$3 " +
        "уваd уве8b увиa увс8 увь2 уг$19 уга35 угв4 уге9 уги2b угл3b уго83 угр1c угу12 уд$b уда188 удв6 уде35 удж6 уди9c удк3 удл3 удм2 удн1b " +
        "удо97 удс2 уду1b удшb уды4 удь7 уе$2 уел3 уем19e уен2 уер3 уес7 ует180 ужа53 ужб42 ужд2f ужеe9 ужиb1 ужкa ужн48 ужо9 ужс6 ужч4 уз$3 " +
        "уза5 узб9 узе24 узи52 узк80 узл34 узнb узо3f узс14 узу2 узч12 узы33 узь3 уи$3 уил3 уир2 уй$2 уйг6 уйн5 уйс5 уйт6f уйю3 уйя3 ук$22 " +
        "ука134 укв2f уке4 укиe уклb укм7 уко5e укр1d укс5 укт75 уку8 укх2 укц26 ул$20 ула2b уле64 ули57 уло15 улс4 улт5 улу39 улыd ульce улюc " +
        "уля82 ум$1d ума33 умб9 умеfa умиf умл2 умм20 умн12 умо1b умп3 умр4 умуa умф4 умч3 умы17 умя6 ун$1d уна39 унг4 унд56 уне6 унж2 уни88 " +
        "ункad унн2f уно19 унс6 унтc уну5 унц8 уны3 унь7 уо$3 уок4 уол5 уор3 уп$14 упа51 упе3e упи3e упкf упл39 упнd0 упо57 упп77 упрf3 упу8 " +
        "упы3 упя4 ур$24 ура8e урб3 урв5 ург14 урдb уре29 ури39 урк10 урм5 урн74 уро8b урп3 урр8 урс98 урт2 уру28 урц5 урч2 уры3b уря2 ус$43 " +
        "уса24 усе2c уси26 уск156 усл48 усм2c усн28 усо26 усп55 усс1c уст426 усы7 усь4 усяe ут$70 ута7c утбf утв1d уте54 ути63 утк1c утнf уто88 утр58 " +
        "утс94 уту12 уты5c уть47 утю2 утяa уфе40 уфф17 уфх2 ух$e ухаb ухи4 ухм2 ухо10 ухп2 ухс7 ухуc уце3 уцу2 уч$7 учаe3 учеdd учи61 учкe " +
        "учн1c учо5 учр3 учт6 учу2 учш50 уш$2 уша19 уше36 уши4d ушк1e ушнb ушт5 уща20 уще189 ущи53 ущнa ущуc уэ$3 уэй4 уэл4 уэн4 уэр2e уэт4 " +
        "ую$4b5 уюс16 уют9f ующ26c уя$12 уяз1b фа$19 фабc фав13 фаз17 файa9 фак4c фал5 фам6 фанb фар12 фасa фат3 фау4 фга7 фе$a фев6 фед14 фей3f " +
        "фек42 фел3 фен2 фер77 фес24 фет2 фи$c фиа3 фиг5b фид25 физ35 фииe фий5 фик1c6 филda фин2c фио13 фир7 фис14 фиц3e фич70 фиш2 фию4 фияe " +
        "фл$2 фла40 фле7 фли3e флу3 флэ5 фм$3 фме8 фми2 фно4 фны4 фо$7 фов8 фогa фок16 фол12 фонc2 фор1a2 фос2 фот32 фр$9 фраb1 фре14 фри1a " +
        "фроf7 фру17 фры7 фт$14 фтаc фтоc фты5 фуз6 фук2 фулd фун69 фур2 фут12 фф$4 ффе43 ффи2e ффу6 фхц3 фцс2 фы$5 фью2 ха$18 хаб4 хаг3 " +
        "хад2 хаз2 хаи2 хай6 хак5 хал5 хам11 хан37 хар2d хауf хаш4 хая6 хбу4 хва66 хво11 хе$3 хей3 хел9 хем31 хен3 хер6 хет3 хешd хжи3 " +
        "хи$21 хивb6 хид3 хие3 хии5 хий5 хик2 хил9 хим5 хин6 хип2 хир7 хит20 хих2 хич4 хию2 хия5 хко6 хла7 хле6 хло4 хма8 хме17 хми4 " +
        "хмо3 хму5 хмы3 хне18 хни24 хно1c хня4 хо$13 хоб2 хов1d хог3 ход30d хое2 хож3e хозc хой7 хок3 хол9 хом12 хон6 хооb хоп2 хор1d хос10 " +
        "хот1d хочa хпо3 хпр2 хпу2 хр$6 хра165 хре5 хри2 хроc5 хск4 хстb хся42 хт$4 хте5 хто6 ху$10 хуа9 худ10 хук2 хун2 хут2 хуц2 хуш3 " +
        "хфа3 ххх2 хцч3 хши3 хър2 хэш22 ца$7a цам29 цан10 цап2 царb цат52 цах12 цаю3 цбе3 цв$3 цве65 це$2f цев2d цег6 цед1c цейe целa2 цемb " +
        "цен170 цеп2a цер12 цес7d цет1b ци$b циа106 циб2 цие78 ции261 ций71 цик3c цилa цим6 цин8 цио105 цип5 цир69 цит2 циф72 циюe8 ция202 цкаf цки19 " +
        "цко2 цо$8 цовe цог2 цомd цп$6 цпу5 цс$9 цу$35 цуе2 цуз14 цур2 цус3 цчш2 цы$43 цып2 цыя6 ча$41 чав2 чаг3 чад4 чаеd2 чаи2 чай4d " +
        "чак4 чал85 чам11 чан6e час12c чат179 чах7 чаш3 чащ4 чаю9b чая1e чво2 че$2c чеб13 чев2f чег17 чееc чез13 чей32 чек20 чел12 чем25 чен543 чер16d " +
        "чес431 чет146 чех4 чеч28 чеш8 чжу4 чжэ2 чи$3f чив106 чие2c чии6 чий15 чикd5 чил72 чим19 чин9c чис141 чит1f4 чихe чищ26 чия15 чк$2 чка66 чке1c " +
        "чки40 чко40 чку1c чле36 чн$2 чнаac чне16 чни55 чно262 чну44 чны248 чок16 чомa чоу4 чре8 чт$3 чта8 чте39 чти1f что55 чту4 чты8 чу$2a чуа4 " +
        "чувb чуд7 чуж9 чун2 чут2 чущ3 чую5 чч$8 чша5 чше37 чши12 чшу2 чшщ2 чь$1e чье2 чьи4 чьтc чью3 чья4 ша$35 шаб2f шав2 шаг20 шад7 " +
        "шае3d шаи2 шай4 шал1d шам7 шан1b шап2 шарf шас2 шат3f шаф3 шах6 шаш2 шаю47 шая36 шве19 шду2 ше$48 шев3 шег5c шед2f шее39 шей54 шек9 " +
        "шел2c шем2f шен2b0 шеп4 шер9 шес5c шет1b шеу5 шечb ши$1e шиб6e шивb5 шид2 шие45 ший5e шил38 шим47 шин6d шир17f шис9 шит6f шифe3 ших4f шка2f " +
        "шке9 шки18 шко16 шку5 шла19 шле12 шли18 шло25 шлы9 шлю1c шми3 шмо2 шна8 шне2c шни3b шно34 шну5 шны1d шнюa шняe шо$6 шог7 шое8 шойc " +
        "шок4 шом8 шон3 шот7 шоу4 шпи21 шри25 шру4f шск12 шт$4 шта33 ште4 што5 штр20 штуa штх3 шу$15 шуа2 шук2 шум8 шут3 шущ3 шую1f шщъ3 " +
        "шь$a шью4 шэд2 ща$a щад3 щае9a щай8 щал8 щам5 щанa щат59 щах3 щаю49 щая124 ще$1c щег129 щед1e щееfb щеи3 щейd3 щел1e щемd9 щен324 щеп2 " +
        "щесc5 щет6 щи$a щив3 щие150 щий1ac щикac щил14 щим110 щин11 щит5d щих135 щищ68 щни15 щно16 щря2 щу$6 щущ4 щую7b щъы3 щь$6 щью9 ъед52 ъек6e " +
        "ъем5e ъеш2 ъыь2 ъя$2 ъяв65 ъясa ъят5 ыба9 ыбе13 ыби17 ыбо43 ыбр80 ыбы3 ыв$13 ыва3b9 ыве26 ывн1e ыво52 ыву2 ывч2 ывш7 ывы4 ыгл6 ыгр3d " +
        "ыда65 ыдв3 ыде89 ыду3c ые$8e9 ыез6 ыем2 ыже2 ыжк8 ыжн2 ыжо3 ызв47 ызо4a ызс2 ызы55 ыи$2 ыиг3 ый$b68 ыйдa ыйи3 ыйтa ык$f ыка48 ыке7 " +
        "ыкиf ыкл54 ыко52 ыку8 ыл$13 ыла3c ылиf ылк56 ыло1b ыль8 ыля3 ым$4f9 ыма2 ыме5 ыми283 ымн2 ымс2 ымч2 ымя5 ына2 ыне3 ыни4 ынк3 ыно5 " +
        "ынс7 ыну8 ынь4 ыня3 ыпа2 ыпе2 ыпл4 ыпо128 ыпу3d ыр$2 ыра40 ырг2 ыре2b ыри6 ыро26 ырьa ыря2 ыса3 ысв3 ыси1c ыск5 ысл8 ысо64 ыст52 " +
        "ысу2 ысшd ыся5 ыт$11 ыта65 ыте4 ыти8a ытк2d ытн9 ыто66 ыту4 ыты5c ыть30 ытя7 ых$8b5 ыхо61 ыхф2 ыцк2 ыча4 ыче8 ычи39 ычк11 ычн4c ыша2a " +
        "ыше70 ыши7 ышк14 ышл12 ышн2 ышс6 ышу2 ышь8 ыщеc ыьэ2 ыя$10 ыяв1c ыял3 ыясc ьа$2 ьба5 ьбо24 ьбу3 ьва6 ьви6 ьга4 ьги9 ьго12 ьд$3 " +
        "ьда7 ьде2 ьдиb ьдо4 ье$18 ьевb ьег6 ьед2 ьез13 ьей3 ьем2 ьен7 ьер9 ьетe ьеф5 ьже4 ьза3 ьзе3 ьзо16a ьзуb8 ьзя7 ьи$e ьим3 ьих3 " +
        "ьйо4 ька11 ьки24 ько2f ькуc ьм$5 ьмаa ьме16 ьмиf ьмо12 ьмы4 ьмя4 ьн$2 ьна102 ьне26 ьни34 ьно4be ьну7b ьны421 ьню2 ьон9 ьс$5 ьса8 ьси3 " +
        "ьскcf ьсн3 ьсо7 ьст22 ьсы2 ься24b ьт$2 ьта41 ьте81 ьти5b ьто3 ьтр7c ьтс2 ьту4 ьты3 ьфа7 ьфу2 ьца17 ьце1b ьцо6 ьцу4 ьцы4 ьча2 ьче3 " +
        "ьчж2 ьчи4 ьша19 ьше57 ьши4c ьшо21 ьшуa ьэю3 ью$b8 ьюж2 ьюл2 ьют59 ьюф3 ьюэ2 ья$24 ьяв2 ьяг2 ьям9 ьян21 ьяр5 эб$4 эба2 эби2 эве3 " +
        "эво4 эвр17 эге2 эд$2 эди2 эдо3 эй$7 эйд2 эйк3 эйн2 эйп2 экв28 экз25 эко13 экр5c экс85 эл$4 эла4 эле88 элл7 элп3 элт3 эльe эма2 " +
        "эмбa эми7 эмо5 эмп2 эму27 эн$4 эне3e энк2 эно4 энс2 энт6 энь2 эп$2 эпи2 эпо5 эпс4 эр$e эраb эрг2 эре4 эри4 эрм2 эрн3 эроb " +
        "эрт5 эру3 эры7 эсв3 эск19 эсп3 эстf эт$3 эта2b эти1a этн3 это3a этр2 эту8 эты2 эфиd эфф4a эха3 эхо5 эш$14 эшаf эше13 эши6e эшм2 " +
        "эшу6 эюя3 эя$2 юба4 юби2 юбл2 юбо21 юбу5 юбы11 юг$2 юго7 юд$2 юда20 юде19 юдж6 юди4 юдь2 юдя3 южн1b южо2 юз$a юза8 юзиb юзоc " +
        "юзу4 юзы3 юй$2 юйм11 юк$3 юко9 юкс8 юл$3 юла2 юле2 юлл5 юль3 юля2 юм$2 юмо5 юн$2 юнеd юни10 юно6 юнь8 юня2 юпи2 юра3 юри8 " +
        "юрк3 юрн2 юс$9 юса3 юсе2 юси5 юсн2 юсо3 юсс2 юст5 юсу2 юсь4 юся15 ют$126 юта3 юте5b ютн13 ютс1ba юты4 юфа3 юч$d юча138 ючв2 юче217 " +
        "ючиd7 ючк5 ючо7 ючу7 юшк8 ющаcc юще2af ющи3d4 ющу5b юэй2 юю$25 юя$3 ябл2 ябрe яваb яви27 явк12 явлde явн31 яво4 явш9 явя3 яга4 ягкe " +
        "яго2 яд$11 ядаf ядеb ядиe ядк33 ядн38 ядо41 ядр1a яду2 яем1a0 яет179 яжа7 яже26 яжи6 яжк4 яз$2 язаdb язв1b язе5 язиa язк1f язн14 язо4 " +
        "язуd язы70 язьc язя4 яинe яйт24 яка3 яко5 яку9 ял$18 яла1f яли1c яло19 ялс12 яль3 ям$bd яма9 ямб4 яме3 ями113 ямо36 ямс4 яму7 ямыb " +
        "ян$11 яна10 янвa янг5 янд7 яне3 яни5e янк5 янмa янн5b яно10 янс28 яну12 янцc яныc яо$2 яоц2 япо1b япр2 яр$9 яраb ярд5 яре4 ярк17 " +
        "ярл1a ярн23 яро8 ярс5 яру6 яры4 яс$6 яса7 ясе6 яск3 ясн20 ясо4 яст2 ясы2 ясь8 яся34 ят$4b ята20 яте14 яти62 ятк6 ятн3d ято7a ятс49 " +
        "яту9 яты36 ять169 яфа3 яфи3 ях$eb яц$7 яцаb яце8 яци55 яцы4 яч$5 яча4 яче19 ячи7 ячн9 ячу4 яшн2 яща20 яще7f ящиc6 ящуa яэк3 яю$5 " +
        "яютae яющ146 яя$70 ";
    public const string En =
        "^aa39 ^ab7f ^ac190 ^ad12b ^ae20 ^af3c ^ag46 ^ahe ^ai30 ^ak11 ^al18b ^am27 ^anc4 ^ao6 ^ap11f ^aq5 ^arc8 ^asfd ^at9b ^au162 ^av31 ^aw17 ^ax5 ^ayb " +
        "^az18 ^ba168 ^bb2f ^bc23 ^bd1b ^bef0 ^bf14 ^bg3 ^bh11 ^biad ^bj2 ^bl61 ^bm9 ^bn2 ^bobd ^bp7 ^bra4 ^bs17 ^btc ^bu99 ^bwd ^by31 ^bz4 ^ca24e " +
        "^cb1b ^cc31 ^cd24 ^cedd ^cfd ^cg7 ^ch1a0 ^ci4a ^cj7 ^cl166 ^cm31 ^cnf ^co613 ^cp1b ^crfe ^cs29 ^ct26 ^cua7 ^cv3 ^cw13 ^cx2 ^cy26 ^cz4 ^daf9 " +
        "^db1a ^dc26 ^dd54 ^de3b6 ^df14 ^dh32 ^di29e ^dje ^dk3 ^dl26 ^dm15 ^dn1a ^do119 ^dp1b ^dr85 ^ds67 ^dt18 ^du60 ^dve ^dw1f ^dxf ^dy12 ^dz1f ^ea40 " +
        "^eb12 ^ec31 ^ed28 ^ee15 ^ef3c ^eg14 ^eh6 ^ei21 ^ej3 ^ekc ^el39 ^em56 ^en1e5 ^eoc ^ep25 ^eq22 ^er44 ^es63 ^et44 ^eu1e ^ev9a ^ew7 ^ex223 ^ey6 " +
        "^ez3 ^faf7 ^fb2 ^fcb ^fd15 ^fe61 ^ff16 ^fg5 ^fi1bc ^fl7a ^fme ^fn6 ^fo167 ^fp13 ^fq7 ^frb5 ^fs2e ^ft1f ^fu6d ^fv5 ^fw22 ^fy6 ^ga80 ^gb19 " +
        "^gce ^gda ^ge135 ^gf2 ^gg27 ^gh26 ^gi2e ^gj2 ^gl3d ^gma ^gn6 ^go37 ^gp37 ^gra7 ^gsc ^gt13 ^gu5e ^gwe ^gx2 ^gy10 ^ha123 ^hb10 ^hc5 ^hdd " +
        "^hedb ^hg2 ^hh12 ^hi8e ^hk19 ^hl25 ^hm27 ^hn1a ^hobd ^hpa ^hr1e ^hs1d ^ht21 ^hu23 ^hv3 ^hw18 ^hx1d ^hy37 ^hzc ^ia1a ^iba ^ic3b ^id6f ^ie12 " +
        "^if1f ^ig22 ^ih6 ^ii15 ^ij2 ^ik18 ^il13 ^imb6 ^in421 ^io112 ^ip6a ^iq2 ^ir27 ^isde ^it50 ^iu4 ^iv6 ^iw6 ^ix4 ^iy5 ^iz8 ^ja2e ^jc3 ^jec2 " +
        "^jf3 ^jha ^ji18 ^jj1f ^jo43 ^jp3 ^jsa ^jt6 ^ju3b ^jw4 ^jy9 ^ka89 ^kbd ^kc5 ^kdb ^kec7 ^kg2 ^kh46 ^ki43 ^kj2 ^kk8 ^kl5 ^kme ^kn13 " +
        "^ko3e ^kp11 ^kra ^ks17 ^kte ^ku1a ^kv3 ^kw1a ^kxe ^ky12 ^la12a ^lb7 ^lc18 ^ld1a ^led7 ^lfb ^lh10 ^li13a ^lj7 ^ll19 ^lm4 ^ln3 ^lo1f7 ^lp1f " +
        "^lrb ^ls21 ^ltf ^lu4a ^lv4 ^lw12 ^lx5 ^lye ^lz5 ^ma2cd ^mb26 ^mc19 ^md16 ^me133 ^mf24 ^mg25 ^mh6 ^mi134 ^mk8 ^mle ^mm3e ^mn4 ^mo140 ^mp1b " +
        "^mrd ^ms73 ^mt17 ^muab ^mv2 ^mw12 ^mx8 ^my53 ^na106 ^nb2b ^nc5f ^nd61 ^ne1d2 ^nf20 ^ng4a ^nh14 ^ni56 ^nj29 ^nkc ^nl26 ^nm26 ^nn3c ^no1b8 ^np47 " +
        "^nq5 ^nr52 ^ns5b ^nt91 ^nu75 ^nv19 ^nw1a ^nx4 ^ny37 ^nz1d ^oa9 ^ob57 ^oc4e ^od15 ^oe12 ^of56 ^og9 ^oh5 ^oid ^oj5 ^okd ^ol32 ^om2b ^onfd " +
        "^oof ^opc6 ^or7f ^os24 ^ot21 ^ou7b ^ov58 ^ow2e ^ox7 ^oy5 ^oz2 ^pa29a ^pb5 ^pc20 ^pd24 ^pe13e ^pf1d ^pg3 ^ph5d ^pi85 ^pk15 ^pl8d ^pm11 ^pn12 " +
        "^po144 ^pp1c ^pq3 ^pr418 ^ps2c ^pt12 ^pu96 ^pw1f ^px6 ^pya ^pz2 ^qa21 ^qc6 ^qd3 ^qe6 ^qf5 ^qg2 ^qhc ^qic ^qm29 ^qn2 ^qo11 ^qp10 ^qs9 " +
        "^qt8 ^qu15d ^qwa ^qx2 ^qy10 ^racc ^rba ^rc10 ^rd20 ^re8c7 ^rfa ^rg5 ^rhb ^ri65 ^rj3 ^rk4 ^rl7 ^rm12 ^rna ^roe7 ^rp24 ^rr29 ^rs29 ^rt2e " +
        "^ru84 ^rv2 ^rw10 ^rx4 ^ry8 ^rz2 ^sa13b ^sb5 ^sc10a ^sd2b ^se3b1 ^sf7 ^sga ^sh17e ^si131 ^sj3 ^sk37 ^sl57 ^sm55 ^sn33 ^sob8 ^sp10c ^sq49 ^sr4e " +
        "^ss7f ^st224 ^su25b ^sv13 ^sw4b ^sx7 ^sy11f ^szf ^ta1f8 ^tb10 ^tc70 ^td42 ^te124 ^tf2a ^tg12 ^th152 ^ti11c ^tj2 ^tkc ^tl46 ^tm3c ^tn28 ^toe0 ^tp91 " +
        "^tq5 ^tr20f ^ts95 ^tt6a ^tu74 ^tv13 ^tw29 ^tx23 ^ty38 ^tz14 ^uad ^ub4 ^uc9 ^ud14 ^ued ^uf2 ^ug4 ^ui1e ^uke ^ul14 ^um13 ^un261 ^uo4 ^up9e " +
        "^ur2a ^use6 ^ut2c ^uue ^ux3 ^uy5 ^uz7 ^vac2 ^vb6 ^vc10 ^vd10 ^veac ^vf5 ^vg2 ^vh8 ^vid5 ^vl7 ^vma ^vn2 ^vo5b ^vp8 ^vr7 ^vs28 ^vtb " +
        "^vu14 ^vw4 ^vy7 ^waaf ^wbe ^wc8 ^wd10 ^we75 ^wf16 ^wh5a ^wi13e ^wla ^wmf ^wn7 ^wo7d ^wp15 ^wr45 ^ws39 ^wt6 ^wuc ^ww13 ^wx2 ^wy3 ^wz5 " +
        "^xa1e ^xb9 ^xc14 ^xd8 ^xed ^xff ^xg2 ^xh3 ^xi15 ^xj5 ^xl2 ^xm10 ^xn2 ^xoe ^xpb ^xr3 ^xs9 ^xt8 ^xu5 ^xv3 ^xw8 ^xx15 ^xye ^ya53 " +
        "^yc3 ^ye43 ^yf4 ^yi27 ^yl3 ^ym2 ^yn7 ^yo28 ^ypb ^yr3 ^ys2 ^yt3 ^yu22 ^yv2 ^ywd ^yy13 ^za1e ^zb2 ^zc2 ^ze24 ^zf2 ^zh2e ^zi14 ^zj2 " +
        "^zl4 ^zo19 ^zq2 ^zsc ^zt6 ^zud ^zw5 ^zy9 ^zz23 aa$86 aaa8 aab8 aac9 aad21 aae2 aaf9 aag2 aah2 aaie aak3 aal7 aama aan18 aap5 " +
        "aarc aas4 aat7 aau2 aav2 aay3 ab$23 aba4a abbe abc13 abd5 abe19 abh2 abi64 abk3 abl283 abn8 abo41 abr5 abs15 abu9 abw3 abyb ac$42 " +
        "aca10 acc118 ace130 ach111 aci2b ack27d acl28 acm7 acn3 acof acpc acq1d acr24 acs3 act257 acu6 acv2 acw2 acy1c ad$bd ada72 adb21 adc27 add112 " +
        "adebd adf12 adg7 adh11 adi81 adj19 adk4 adl33 adm51 adn2 ado32 adpd adr17 ads35 adtb adu4 adv42 adw2 ady31 ae$1d aea7 aed2 aee3 aef6 " +
        "aeg2 ael8 aem4 aene aerb aes10 aet3 aexa af$23 afa4 afd2 afe11 aff20 afg6 afic afl2 afo5 afrc afs5 aft19 afu6 ag$48 aga39 agbe " +
        "agc6 agd9 age2c6 agf3 agg1c agh13 agi2c agl2 agm1e agn23 ago1c agr30 ags51 agt7 aguf agv4 ah$30 aha1b ahe6 ahh2 ahi9 ahl3 ahm4 aho9 " +
        "ahr3 ahs2 aht2 ahu2 ai$44 aia3 aicf aid1a aif2 aig3 aii4 aij6 aika ail15c aim1b ain1a3 aip3 air48 ais13 ait48 aiw4 aiy2 aj$3 ajae " +
        "aje2 aji4 ajob aju3 ak$38 aka1e akc3 ake58 akh1c aki18 akk3 akma ako7 akp6 akr3 aks10 akt4 aku8 al$34f ala77 alb11 alc29 aldf ale82 " +
        "alf2a alg40 alh5 ali206 alj3 alk1a all357 alm24 aln11 alo47 alp31 alr1c als70 alt7a alu87 alv13 alwd aly1e am$d0 ama4e amb20 amc6 amd15 ame304 " +
        "amh3 ami60 aml3 amm2c amn4 amo17 amp82 amr4 ams35 amt6 amue amz5 an$200 ana146 anb7 anc14e and1f8 ane49 anf2 ang1b0 anh4 ani94 anj2 ank1f " +
        "anl4 anmb ann95 ano2c anpb anq2 anr4 ans123 antd1 anu4a anv3 anw4 anx2 any39 anz4 ao$13 aoa2 aon2 aop2 aos2 aou2 ap$c6 apa38 apc13 " +
        "ape20 aph60 api62 apld apn7 apo18 app1b2 apr1a aps3c apt48 apu5 apv6 apw7 apyb aq$c aqe2 aqp2 aqua ar$d9 arafa arb1e arc4d ardf6 are122 " +
        "arfb argc8 arh2 aria2 ark52 arl10 arm20 arn35 aro19 arp13 arq3 arr51 ars7b art187 aru12 arv8 arw6 ary71 as$8c asa1c asc19 asd3 ase10b asf3 " +
        "ash77 asi3c asj2 ask199 asl2 asmd asn11 aso35 asp17 asq4 asr8 ass199 aste6 asu29 asw8 asy35 at$da ata13d atb2 atc95 atd2 ate62d atf10 athdf " +
        "ati71b atk4 atl11 atm12 atn2 atoa6 atrc ats25 attde atu7c atv4 atw2 aty25 au$18 auc4 aud73 aue2 augd aul73 aum5 aun46 aurf aus70 aut16e " +
        "aux5 av$13 ava3b avd2 ave8f avf2 avg8 avi96 avl4 avm2 avoc avs5 avu3 avyc aw$1f awa32 awe2 awib awk2 awn5 awo3 awr5 aws2 awt4 " +
        "ax$47 axa5 axc3 axd7 axe6 axf5 axi16 axlf axm2 axpb axra axs17 axu2 axv5 ay$a6 aya3b ayb10 ayc8 ayd3 aye62 ayf2 ayg3 ayh3 ayi14 " +
        "ayl18 aymc ayn8 ayo1d ayp4 ayr2 ays41 ayt4 ayu2 az$11 azac aze7 azh2 azia azo2 azr2 azs2 azua azy2 ba$2e baaa babc bac150 bad2b " +
        "baf7 bagb bah8 bai10 baj2 bak6 bal31 bamd ban3d bape bar3c bas77 bat41 baud bax3 bay3 bb$6 bba8 bbc3 bbeb bbi11 bbl3 bbo5 bbr8 " +
        "bbua bby7 bc$f bca16 bcc8 bcd23 bcl5 bco7 bcp5 bd$10 bda5 bdc5 bdef bdib bdo3 be$28 bea11 bec18 bed1c bee1c bef10 beg32 beh1b bei14 " +
        "bek8 bel41 bem4 ben16 bep6 bera8 bes14 bet1d beu3 bex2 bey7 bf$7 bfa2 bfc3 bfd6 bfe4 bff5 bfi4 bfl2 bfo5 bfp2 bfr4 bfs2 bg$4 " +
        "bga2 bgr7 bgs6 bgt2 bha7 bhd3 bhe3 bho5 bhu3 bi$10 bia22 bib3 bic1d bide bied big13 bik3 bil68 bim2 bin9c bio18 bip4 biq3 birc " +
        "bisb bit50 bix3 bj$7 bja2 bje8e bji3 bjo5 bjr2 bjs2 bk$5 bke9 bkh3 bku4 bl$4 bla28 ble2e9 blf4 blg3 bli74 blj2 blo4f blt2 blu5 " +
        "bly2c bm$d bma3 bme3 bmi22 bmo3 bmu2 bn$9 bna3 bne11 bno9 bnu2 bo$16 boa1c bob4 boc4 bode bog7 bol1b bom2 bonc boobf bop9 bor3a " +
        "bose bot11 bou78 bove bow4 box1e bp$3 bpo6 bpr16 bpsb bpu2 bqu2 br$c bra39 brd2 bre36 bri2d brm4 bro45 brs2 brub bs$30 bsa4 bsc41 " +
        "bse20 bsh3 bsi6 bsk2 bso12 bsp5 bsr2 bss9 bst31 bsu3 bsye bt$a bta14 bte4 btg3 bth7 bti5 btm7 bto3 btp2 btr11 bts4 btt2 btu2 " +
        "bty7 bu$15 bua2 bub4 buc7 bud4 bue2 buf53 bug4e buh4 bui2a buk2 bul15 bun5 buo9 bup6 bur1a bus18 but4f buv4 bux3 bvi3 bvr2 bw$2 " +
        "bwa8 bwe6 bwf2 bwi2 bxm3 by$1a bya3 bye2 byh2 byi6 byl2 byn3 byo4 byp15 byr5 bys5 byt53 byx3 byy3 bzh2 bzu2 ca$36 caa6 cab1b " +
        "cac8d cadd cai8 cak2 cal237 cam14 canef cao3 cap7e car6f cas5a cat2b4 cau2e cav5 cay7 cb$15 cba3 cbc4 cbd4 cbea cbi4 cbm3 cbn2 cbo4 " +
        "cbp4 cbsa cbu4 cc$1e ccae ccc2 ccd8 cceed cch10 cci6 ccla ccm5 cco75 ccr2 ccs11 cct8 ccu34 cd$19 cda7 cdb4 cdd5 cde12 cdf7 cdh5 " +
        "cdid cdm7 cdo5 cdp7 cdq3 cdr3 cdsf ce$235 cea27 ceb9 cec1b ced94 cee52 cef1b ceg5 ceh8 cei76 cek4 cel71 cem2d cen89 ceo18 cep58 ceq3 " +
        "cer136 ces230 cet1f ceu7 cev8 cew7 cexb cf$8 cfa2 cfb2 cfg26 cfi4 cfo4 cfr3 cg$9 cga2 cgm5 cgr3 ch$d3 cha1ae chc14 chd23 che1c1 chf8 " +
        "chgb chha chie2 chk12 chm15 chnb cho50 chp3 chr3b chs10 cht1b chu26 chv3 chw8 chy11 ci$e cia43 cib3 cidd cie41 cif5e cig2 ciib cila " +
        "cim14 cin4e ciof cip33 cir23 cis11 cit28 cje2 cjk7 ck$141 cka6b ckbc ckc7 ckd12 ckee1 ckf14 ckg2d ckh3 cki32 ckk3 ckl13 ckmc ckn17 cko18 " +
        "ckp1b ckq6 ckr9 cks74 ckt7 cku52 ckw14 cl$17 cla99 clc2 cle88 clf7 clidd cln2 clob8 clr2 cls1c clu8b cm$17 cmaf cmc3 cmd2d cme17 cmf3 " +
        "cmg6 cmk2 cml9 cmm4 cmo5 cmp16 cms3 cmt2 cn$22 cnae cnc3 cne6 cng2 cnn2 cno2 cnsa cnt2 co$1c coa6 cob2 coc6 cod70 coe10 cof5 " +
        "cog18 coh3 coi8 col9b com30f con3fd coo1d cop77 cor10b cos13 cot4 cou10a cova6 cow4 cp$38 cpa3 cpc2 cpe3 cph7 cpid cpm2 cpo8 cpp3 cpr20 " +
        "cps5 cpu1d cpv6 cqu20 cr$15 cra24 crc4 cre15f cricd crl1c crm8 cro67 crp2 crs6 crt5 cru16 cry6b cs$88 csa5 csc2 cse13 csf4 csi24 csp1d " +
        "csr4 css11 cst9 csu2 csva csw2 csy3 ct$12e cta30 ctb2 ctc18 ctd6 cted0 ctf9 cthc cti369 ctl41 ctne ctoaa ctp9 ctr18 cts5b ctt6 ctu37 " +
        "ctv9 ctw4 ctx6 cty5 cu$a cua5 cub8 cue2 cui9 cul2e cum19 cunf cuo5 cup9 cur13d cus3a cut6a cv$c cva4 cvb2 cvd2 cvf4 cvi3 cvt3 " +
        "cw$2 cwa6 cwe3 cwf2 cwi5 cwn2 cwo5 cws5 cx$3 cxf3 cxh2 cy$af cya6 cyc25 cyd3 cye5 cyf4 cyi2 cylc cym2 cyn4 cyp8 cyr8 cys7 " +
        "cyte cze4 czi2 da$59 daae dab15 dac2d dad14 dae4 daf3 dagf dah7 daic dak5 dal20 dam19 dan41 dap3e dar36 das16 dat1d8 dau4 davd dax7 " +
        "day31 db$25 dba16 dbc8 dbe2 dbf3 dbg8 dbh3 dbi5 dbl4 dbm3 dboc dbr3 dbs7 dbt2 dbu8 dby1f dc$25 dca34 dcc7 dcd3 dceb dcf5 dcha " +
        "dcl1f dcm3 dcna dco4c dcp5 dcr9 dcsf dct9 dcu3 dcv5 dd$26 dda2b ddb4 ddca ddd8 dde37 ddf6 ddh10 ddi45 ddj2 ddl15 ddm5 ddn3 ddoa " +
        "ddpc ddr79 dds14 ddtb dduc ddw2 de$15d dea44 deb4a dec7c ded11b dee14 defc2 degb deh1b dei4 dej2 dek4 del103 dem24 den10e deo31 dep59 deq4 " +
        "der1c3 des105 det73 deu2 devaf dew6 dex97 df$1a dfac dfc4 dfe3 dff3 dfi18 dfo8 dfp4 dfr19 dfs8 dg$2 dge27 dgi4 dgl5 dgm3 dgr2 dgu2 " +
        "dgy2 dh$1a dha1a dhc12 dhe6 dhg6 dhh9 dhi9 dhod dhp4 dhu4 di$2f diae5 dib2 dic73 did2a diec dif6a dig27 dij2 dil5 dimd din154 dio29 " +
        "dip5 dire3 dis219 dit95 diu10 div1d dix5 dj$5 dja3 dje6 dji3 djo8 dju15 dk$4 dka3 dke13 dl$f dla11 dld2 dlecc dlgd dli4b dll1a dlo12 " +
        "dls4 dlyf dm$1e dma1d dmc5 dme14 dmg3 dmi45 dml5 dmm2 dmo8 dmp4 dms6 dmu3 dn$2f dna16 dnc4 dnea dni3 dnk2 dno5 dns35 dnt4 dnu3 " +
        "do$3d doa8 dob4 doc22 dod4 doef dof2 doge doh7 doi9 dolb dom74 don4b doo8 dop7 dor2b dos14 dot1c dou1a dov4 dow10a dox4 doz2 dp$1a " +
        "dpa40 dpc6 dpe10 dpf2 dpib dpl3 dpn7 dpo2a dpp3 dpr19 dps3 dpu6 dpv2 dqp3 dqu8 dr$39 dra58 drc4 drd4 dre86 drf3 dri68 drm2 dro26 " +
        "drs6 drt7 drue drv5 ds$17b dsa1f dsb2 dsc13 dsd3 dse16 dsgd dsh13 dsi10 dsk8 dsm2 dsn5 dso7 dspb dsra dssa dst48 dsud dsv2 dsy6 " +
        "dt$9 dta2c dtc10 dtd4 dte17 dth1f dti10 dto19 dtr14 dts7 dtv2 dty9 du$18 dua7 dub2 duc47 due7 dui3 dul43 dum1f dun19 duo5 dup2b dur2e " +
        "dus13 duta dux3 dv$6 dvad dvd6 dve25 dvf7 dvi12 dvm3 dvt2 dwa17 dwe2 dwf2 dwi16 dwme dwo16 dwr9 dwv2 dx$c dxd2 dxg6 dxm2 dxo2 " +
        "dxp2 dxt2 dxw2 dy$2b dya3 dyd2 dye3 dyf2 dyi3 dyn12 dyo4 dyr3 dytb dyw2 dz$3 dza4 dze7 dzh3 dzi2 dzj2 dzo5 dzu3 dzw2 dzz2 " +
        "ea$2e eab1c eac61 ead16c eafb eagd eai2 eak38 eal7a eam76 ean5f eap3b eara6 easd2 eatf5 eau26 eav1b eawc eay2 eb$12 eba2b ebc5 ebd3 ebe8 " +
        "ebg2 ebi9 ebo34 ebrc ebs6 ebu53 ebv3 eby13 ec$62 eca51 ecb5 ecc8 ecd1b ece71 ecf4 ech68 eci8a ecj3 eck62 ecl41 ecm1d ecn9 eco190 ecpe " +
        "ecr4d ecsd ect3c3 ecud9 ecv10 ecw7 ecx3 ecyf ecz2 ed$d4c eda2e edb12 edc4f edd18 edee1 edf14 edg18 edhb edi118 edj2 edkb edl16 edm10 edn12 " +
        "edo31 edp34 edq5 edr30 eds38 edt22 edu67 edv2 edw10 edx5 edz2 ee$c9 eea4 eeb2 eecf eed73 eee6 eef7 eeh3 eei3 eek2c eele eem1c een92 " +
        "eeo7 eep37 eer2f ees14 eet1d eeu3 eevf eex23 eey2 eez12 ef$32 efa64 efc9 efd3 efe8f eff32 efg2 efi94 efl12 efn2 efo25 efp4 efr3e efs19 " +
        "eft1c efu2a eg$2b ega70 egc2 egd4 ege3f egib9 egk5 egl3 egmc ego76 egp7 egr76 egs4 egt2 egue egv2 egy9 eh$5d eha18 ehd9 ehe19 ehi11 " +
        "eho16 ehr2 ehs2 eht2 ehw2 ehy19 ei$1c eic2 eid72 eie2 eif2 eig32 eij2 eil2 eim7 einb7 eio3 eip6 eirb eis7 eit16 eiv27 eiz4 eja5 " +
        "eje12 eji3 ejo7 ejt2 ek$45 eka6 ekb3 eke14 ekf2 ekh3 eki5 ekl3 eks5 ekua el$b2 ela9e elb7 elc9 eld2d ele148 elf28 elg6 elh9 eli80 " +
        "ell76 elm6 eln7 elo7d elp4c elr5 els29 elt27 elub elva ely61 em$7e emaab emb6a emc8 emd8 emee7 emg4 emi2e emkf eml4 emm3 emn2 emo109 " +
        "empa6 emr7 ems56 emt9 emuf emw6 emy5 en$14d ena138 enb7 enc17a end1e2 enec6 enf1c eng7a enhe eni6b enj2 enl22 enm6 enne eno2e enp8 enq3 " +
        "enr2c ensb0 ent74f enu55 env19 enw2 enyf enz2 eo$19 eoa2 eobf eoc3 eof14 eoge eoh2 eoi3 eoka eold eomb eon34 eoo5 eop19 eor25 eosc " +
        "eot3 eou72 eov3 eow7 ep$6e epad9 epcb epd3 epe5a epf6 eph14 epif epl7b epm5 epo71 epp4 epr6a eps16 ept42 epuf epv3 epw5 eq$17 eqe2 " +
        "eqi3 eqp2 equ187 er$6ab era19d erb54 erc9a erd2f ere1e0 erfce erg34 erh11 eri1c3 erk14 erl77 ermdd ern12a ero5a erp57 erqb err166 ers2de ert20a eru24 " +
        "erv179 erw29 erx5 ery104 erz4 es$627 esa24 esb4 esc9c esde ese1bc esf9 esg8 esh68 esi90 esk10 esl5 esm6 esn6 esob3 esp87 esq2 esrb ess399 " +
        "est31f esu9d eswa esx2 esy3f et$1b5 etae3 etb25 etc8b etd39 ete19b etf25 etg29 eth7c etid7 etj3 etk6 etl32 etm29 etn12 eto74 etp41 etq5 etref " +
        "etsd5 ettc6 etu57 etvc etw5c etx9 ety2e etz3 eu$8 euc16 eud4 eue65 euhb eui18 euk4 eul11 eum14 eun1c eup23 eur1d eus19 eut17 euu2 eux8 " +
        "ev$13 eva51 evb2 evd9 eve1ab evic8 evo2b evp4 evr2 evs7 evt11 ew$64 ewa62 ewcb ewd6 ewe21 ewf9 ewh4 ewi3f ewk3 ewla ewm7 ewn10 ewo1b " +
        "ewpc ewr14 ewse ewu4 ewv6 eww3 ex$c2 exa38 exb6 exc74 exeab exf5 exh9 exi73 exk2 exo4 expce exrc exs3 ext142 exw7 ey$9a eyad eybb " +
        "eyc6 eyd7 eyed eyf7 eyg4 eyh4 eyi16 eyk2 eyla eym4 eyn7 eyo6 eyp13 eyr2 eys34 eyt4 eyu7 eyv3 eyw10 ezef ezh5 ezi5 ezo5 ezu3 " +
        "ezz2 fa$a faae fabe fac79 fad4 faibf fak5 fal25 fam15 fap4 fard fas15 fat19 fau5f fav3 fax2 fb$7 fba2 fbk2 fbo3 fc$16 fca6 fcb4 " +
        "fcl3 fcoc fcr5 fd$9 fda5 fdb2 fdc2 fdd2 fde5 fdf4 fdi3 fdp5 fds7 fe$1f fea13 feb6 fec41 fedb feee feh5 fei2 fel4 fem4 fen13 " +
        "fer10a fes14 fet37 fev2 few7 ff$3f ffa2 ffb5 ffc3 ffeac fff32 ffi4c ffl2a ffo6 ffs16 ffu3 ffw3 fg$24 fge2 fgh9 fgi2 fgl2 fgr3 fgx2 " +
        "fha3 fhe5 fho7 fi$13 fia9 fib5 ficd4 fid12 fiecd fif9 fig8b fij4 fil2bb finb7 fip6 fir5e fisd fit8 fiv5 fix54 fiy5 fkt2 fl$7 fla66 " +
        "fld3 fle1b fli2f flo46 fls11 flt7 flu3a flv2 fly2 fm$a fma3 fme8 fmo5 fmp3 fms5 fmt4 fn$5 fna7 fno5 fns2 fo$96 foc6 foe3 fof4 " +
        "foi3 fol43 fom2 fon1a foo12 fop2 for23e fou33 fp$19 fpa1e fpe2 fpo4 fpr2 fpu4 fpv3 fpw2 fq$3 fqdd fr$7 fra8e frc2 frd8 fre7d frf6 " +
        "fri1e frl7 frm2 frn3 fro52 frp2 frr2 frs10 frv2 fs$52 fsc9 fse18 fsf8 fsg3 fsh2 fsi11 fsm7 fso3 fsr11 fst11 fsu10 fsy2 ft$38 ftb9 " +
        "ftc4 fte21 fth8 fti6 ftl2 ftm7 ftpf ftrd ftsf ftt5 ftw13 fty6 ftz4 fu$8 fuk2 fula0 fum2 fun2a fup3 fur8 fusc fut7 fuz3 fva2 " +
        "fve2 fvi3 fw$7 fwa4 fwb2 fwc4 fwe5 fwi9 fwl6 fwo3 fwp10 fx$5 fxf4 fxi2 fxo2 fy$3e fya4 fyb3 fyc7 fye7 fyf2 fyi1c fyn3 fyo5 " +
        "fyp2 fyr9 fys7 fytc fyv2 fyw2 ga$40 gaaa gab10 gac19 gad7 gae4 gaf3 gag2 gai15 gal38 gam2d gan29 gapf gar2a gas8 gatd4 gav2 gaw4 " +
        "gax4 gay4 gaz5 gb$5 gba16 gbea gbi2 gbl4 gbo7 gbp2 gbu9 gby4 gc$9 gcac gchd gci6 gcm8 gco11 gcr3 gcu3 gda6 gdd2 gde7 gdi14 " +
        "gdm5 gdn3 gdod gdu2 ge$196 gea1e gebc gecb gedad gee7 gef1c geg16 gei16 gek2 gel9 gem28 gena8 geo1c gep19 ger113 gesa3 get1ac gev9 gew6 " +
        "gex8 gfa3 gfi2f gfl7 gfo2 gfr2 gfu4 gfw2 ggad gge54 ggi1f ggla ggo7 ggrf ggub ggw4 gh$2f gha19 ghba ghd3 ghe19 ghh3 ghi8 ghl4 " +
        "ghn2 ghod ghp6 ght96 ghuc ghz3 gi$10 gia10 gibc gic1a gidf gie10 gif2 gig4 gii2 gil3 gim5 gin10a gio11 gip7 gis7b git17 giu3 givf " +
        "gix2 gje2 gka5 gke6 gkh3 gki3 gko3 gl$4 gla17 gle33 gli21 glo25 glyf gm$4 gma19 gme31 gmi3 gmo14 gmp2 gms7 gmtb gn$22 gna30 gnc3 " +
        "gne2c gni37 gnk2 gnmf gno47 gns9 gnt2 gny3 go$29 goa4 gob2 goe9 goff gog7 goia gol1d gom5 gon50 gooc gop9 gor37 got66 gou4 gov6 " +
        "gox4 gp$4 gpa12 gpe2 gpi7 gpo19 gpr16 gps2 gpt7 gpu11 gqi2 gqu2 gr$18 gra11f grb2 gre8f gri20 grodf grp13 grs4 grua gs$d1 gsa3 gsc3 " +
        "gse25 gsh4 gsif gsm4 gso3 gsp8 gsr2 gstf gsu4 gsw2 gsy3 gt$c gtaf gtb2 gtc2 gte6 gth2b gtia gto8 gtrd gts2 gty7 gu$13 gua38 " +
        "gue18 gug2 gui41 guj5 gul19 gum10 guna guo19 gup8 gur6d gus8 gut4 gux3 guy2 gva2 gvc2 gve5 gvia gvm2 gvo3 gw$4 gwa8 gwe7 gwi4 " +
        "gwm3 gwrd gx$4 gxi6 gxm2 gy$10 gyaa gye3 gyi3 gyo8 gyp7 gyz4 gza3 gzh3 ha$84 haa21 hab1b hac4 had3f hae5 haf2 hagb hahf hai3e " +
        "haj3 hak12 hal67 ham30 han1ff hao2 hap30 harc3 has5b hat2c haud hav1e hawb hax4 hay6 hba11 hboa hbu2 hcaa hce4 hch8 hcn3 hcoc hcp14 " +
        "hcr2 hcs2 hcu2 hd$e hda8 hdb4 hdc8 hde9 hdia hdm2 hdn2 hdo7 hdr15 hds2 hdx4 hdy3 he$c5 hea76 heb7 hec65 heda6 hee2d hef3 heg3 " +
        "heh22 hei16 hel92 hem41 hen86 hep8 her102 hes75 het1a heu11 hevc hew3 hex1b hey6 hfa6 hfi4 hfl3 hfo5 hfu2 hfw2 hg$5 hgl2 hgp2 hgra " +
        "hgu3 hh$b hha11 hhea hhi3 hhm3 hho8 hht2 hhu3 hi$3c hia5 hib1b hic36 hid28 hie3b hiff hig20 hih2 hii7 hij4 hik5 hil3f him9 hinc7 " +
        "hiod hip26 hir1b his49 hit2a hiv1f hiz4 hk$5 hka2 hkca hkd8 hkeb hki2 hkl5 hkn4 hku4 hl$2 hla9 hle4 hli9 hlk2 hlo4 hlp4 hlu8 " +
        "hlyd hm$12 hma17 hme20 hmic hml2 hmm5 hmn2 hmo11 hms8 hmub hmy5 hn$4 hna11 hnee hnic hnod hns5 hnu3 ho$44 hoa8 hob4 hoc5 hod27 " +
        "hoe7 hog5 hoib hoj3 hok3 hol36 hom19 hon2e hoo3f hop1e hor7a hosc1 hot29 hou40 how69 hox4 hoy2 hp$5 hpa5 hpe2 hpf3 hpi3 hpl2 hpo2 " +
        "hpr7 hpu2 hql3 hqu3 hr$a hra14 hrb2 hre6d hri18 hro5b hrr3 hru5 hs$2a hsa3 hsc3 hsd3 hse10 hsh4 hsi4 hsk2 hsm2 hsp5 hsta hsu3 " +
        "hsy3 ht$59 hta12 htef htg2 hth12 hti3 htm6 htn5 hto19 htr7 htse htt3f htwb htyc hu$2b huad hub2 hue3 hug5 huk2 hul2 hum1f hun26 " +
        "huoe hup5 hur16 hus6 hut2f huv2 huw2 hux4 hv$9 hva5 hvi2 hvo5 hw$2 hwa18 hwef hwf4 hwia hwm4 hwn4 hwob hws2 hx$4 hxa4 hxe3 " +
        "hxi9 hxo5 hxu5 hy$1c hyb4 hyc2 hyd19 hyi2 hyl2 hyp2d hyr5 hys16 hyt5 hyx3 hz$11 hzc3 hzu3 hzw2 hzz4 ia$c7 iab21 iac15 iad9 iaed " +
        "iaf2 iag41 iai2 iak9 ial124 iama ianb7 iap5 iar5 ias1f iatf5 iau2 ib$1c iba4 ibb3 ibe44 ibg2 ibi3a ibl6f ibm5 ibo4 ibr13 ibs4 ibu35 " +
        "ibw3 iby5 ic$168 ica2bb icb9 icc8 icd2 ice12e icf2 icg2 ichd ici5d ick7b icl24 icm11 ico2a icp9 icr1c ics56 ict70 icuf icv3 icw2 icx3 " +
        "icy86 id$2d9 ida5f idc6 idd1c ide179 idf7 idg16 idi25 idl34 idnd idod idpc ids3e idt29 iduc idw7 idx6 idy4 ie$4c iec7 ieda0 ief5 ieg3 " +
        "iel21 iem2 ien107 iep1e ier84 iese4 iet26 ieu6e iev2b iew80 iex26 if$1d ifaa ife40 iff2c ifg2 ifi17a ifl4 ifm7 ifn2 ifoa ifr3 ifs4 ift1a " +
        "ify93 ig$32 iga80 igb2 igc2 ige16 igf4 igg28 ighb4 igi42 igm8 ignd3 igo4 igr1f igs3 igu6e igv3 igz3 ih$3 ihg2 iho3 ihu2 ihv5 ii$43 " +
        "iia2 iid7 iim2 iinb iio2 iis7 ij$2 ija8 ije2 iji5 ijk2 iju2 ik$13 ikaf ike3c ikh3 iki8 ikr2 iku7 iky3 il$94 ila3c ilb9 ilc3 " +
        "ild5c ile2fc ilicd ilk2 ill87 ilm5 iln2 ilo2d ilp6 ilr3 ils19 ilt79 ilu3a ily1b im$3c ima9a imbd imc4 ime19a imf3 img2 imh3 imi9a imm1e " +
        "imn4 imo6 imp92 ims1c imt4 imu2d imv4 in$191 ina128 inb4a incca ind1cb ine1ca inf106 inga0e inh3b ini17f inj10 ink9d inl23 inm15 inn28 ino1c inp46 " +
        "inq8 inr27 ins1a6 int24e inu46 inva5 inw7 inya io$32 iob4 ioc10 iod21 ioea iof29 iog2 ioh3 ioi3 iol13 iomd ionb77 iop13 iore0 ios36 iot13 " +
        "iou20 ip$84 ipa36 ipbd ipcc ipe31 ipf5 ipg3 iph14 ipif ipl24 ipm8 ipo10 ipp34 iprd ips48 ipt88 ipub ipvc ipx3 iq$4 iqn2 iqu22 ir$5b " +
        "ira28 irc22 irde ire16a irga iri26 irk4 irl2 irm18 irn4 iro1c irp5 irq7 irr18 irs36 irt3f iruf irw2 iry9 is$8c isaa1 isba isc99 isd8 " +
        "ise7d isf13 ish9e isia7 isk67 isl16 ism46 isne iso43 isp86 isr10 iss85 ist27d isu12 isv8 isw9 isy4 it$12c ita62 itb5 itc51 itd4 ite172 itf11 " +
        "ith73 iti1be itl20 itm1f itn5 ito3f itpe itq2 itr10 its5a itt60 itu29 itv3 itw2 ity142 itz4 iu$3 iue2 iul2 iumf iun2 ius15 iv$c ivaf6 " +
        "ive1f6 ivi69 ivoa ivs3 iwaa iwe2 iwi4 iwn4 iwr2 ix$64 ixa2 ixb3 ixe20 ixf3 ixh2 ixi6 ixl3 ixq2 ixs2 ixta ixu2 iy$8 iya18 iyec " +
        "iyo2 iz$6 iza63 ize171 izh7 izi1e izo8 izu2 izz3 ja$19 jab3 jac5 jad2 jae2 jak2 jal3 jam3 jan15 jap7 jarb jas2 javd jay4 jce2 " +
        "je$1a jecb1 jee9 jeg2 jeh2 jer10 jeta7 jev3 jex2 jfa5 jfi2 jh$2 jha7 jhe2 ji$b jib6 jid3 jiec jik4 jil2 jin9 jip3 jis3 jit8 " +
        "jix3 jja2 jji8 jjo5 jjua jjy5 jk$7 jkl2 jo$9 job18 joh5 joi2f jol2 jon8 joo2 jop3 jore jot5 jou10 jox3 joy2 jpu2 jre4 jrn2 " +
        "js$4 jse4 jso5 jth7 jto2 ju$6 jua4 jub3 jud4 jul4 jum7 jun18 juo9 jup5 jura jus1d jut3 jux3 jwa3 jy$4 jyo3 jyp3 jyr4 jyt3 " +
        "jyx3 ka$28 kaae kab9 kac9 kad2 kaf5 kag67 kah4 kai6 kak4 kald kam6 kan2a kap17 kar1c kasa kat1b kau3 kav2 kaya kaz6 kb$b kba2 " +
        "kbe8 kbk2 kbl3 kbp3 kbu6 kby3 kca1e kcc3 kch4 kcl4 kco1b kcr6 kcs4 kcu5 kcy2 kdb2 kdc8 kdef kdi8 kdn3 kdo5 kdq2 kdr2 kds8 " +
        "kdu2 ke$4f ked70 kee25 kef4 keh5 kek3 kele kem5 ken4d keo8 kep8 ker8c kes14 ket87 keua kev9 kew4 kex8 key13f kfa4 kfi5 kfl3 kfo10 " +
        "kfr6 kg$8 kgf4 kgr34 kh$11 kha43 khe9 khh4 khif khm8 kho1c khs2 khub khy3 ki$13 kia2 kid1a kied kik2 kil11 kim3 kinb2 kip28 kird " +
        "kise kit19 kiya kja2 kje2 kk$2 kka5 kke6 kki5 kla6 kled klie klm7 klo3 kly5 km$7 kma1d kmc2 kmeb kmg6 kmoa kmp3 kms3 kna8 " +
        "kne7 kni4 kno34 knt6 ko$e koe4 kof8 koi3 koj2 kok3 kol5 kom7 kon11 koo4 kopb kor10 kos5 kot5 kou9 kow3 koy3 kp$4 kpa14 kpea " +
        "kpi2 kpl2 kpo19 kprb kq$5 kqu1f kr$2 kra17 krb2 kre11 kri7 krt2 kru10 ks$98 kscc ksd3 ksed ksf2 ksh7 ksic ksk2 ksl6 ksm2 kspb " +
        "ksr2 kss2 kst37 ksu13 ksw3 kta3 ktf4 kth8 ktia ktmb ktob kts3 kty6 ku$21 kua2 kun9 kuo3 kup72 kur16 kus3 kuta kuu4 kuw3 kuy2 " +
        "kva2 kvi4 kvo3 kw$4 kwa18 kwe5 kwif kwo8 kwr4 kxa3 kxe2 kxw5 ky$6 kya5 kye3 kyi2 kyo6 kyr4 kyu2 la$53 laa14 lab60 lac52 lad16 " +
        "lae3 laf2 lag68 lah7 lai2c laj3 lak8 lal7 lam29 lanb1 lao7 lap1e lar71 lascc lat17f lau4f lav12 law4 lax2 lay100 laz3 lb$6 lba81 lbia " +
        "lbl2 lbn2 lbob lbp3 lby4 lc$5 lca3 lcd2 lce3 lchd lci6 lcl12 lcnb lco1e lcp5 lcr9 lcs2 lct5 lcu13 ld$93 lda17 ldb3 ldcd lde4c " +
        "ldf8 ldi1a ldl5 ldm4 ldn9 ldo8 ldp3 ldr14 lds21 ldt10 ldu3 ldv5 ldw4 le$462 leae9 leb10 lec8f led16f lee32 lef30 leg63 leh9 lei32 lek4 " +
        "lel2d lem5f lena8 leo1f lep22 leq7 lercd les130 let177 leu7 lev69 lew13 lex27 ley2 lez2 lf$20 lfa9 lfhd lfi1f lflc lfoa lfr3 lfs7 lft9 " +
        "lfu3 lfw6 lg$1d lgab lgeb lgi6 lgo1a lgp2 lgre lgs2 lgw4 lh$2 lhac lheb lhi9 lhoa lhu2 li$4b lia43 lib29 lic1c1 lidce lief4 lif48 " +
        "lig3f likd lim64 lin1d3 lio5 lip31 liq4 lis12e litbd liu3 liv27 liw3 lix2 liy2 liz96 lj$2 lja3 lje2 ljo3 lju3 lk$11 lka2 lke19 lkl2 " +
        "lkn2 ll$112 lla6d llb71 llc13 llde lleef llf6 llh3 lli6b llk9 lll6 llm15 llnb llo125 llp14 llrd lls39 llt7 llu16 llv6 llw6 llx6 llyb2 " +
        "lm$2d lma15 lme10 lmf3 lmi3 lml2 lmna lmod lms4 lmv2 lmy2 ln$4 lna17 lne7 lni2 lno4 lns3 lnt2 lnu8 lo$24 loab0 lob29 loc1e8 lod8 " +
        "lof7 log171 lom6 lon74 loo4b lop1a lor25 los7a lot35 lou39 lov16 lowed lox2 loy12 loz3 lp$1f lpa17 lpc7 lpd5 lpe24 lpf3 lph10 lpk2 lpl2 " +
        "lpm5 lpn2 lpo10 lpp2 lpq3 lpr25 lps4 lpt6 lr$8 lra2 lrb4 lrc9 lrd4 lre35 lrfa lrg2 lri4 lrmc lrn3 lro5 lrp5 lrra lrsd lrt7 " +
        "lru6 ls$c6 lsa12 lsb3 lsc10 lsd4 lse26 lshb lsid lsl6 lsn15 lsof lsp7 lsr2 lst1c lsu9 lsy8 lt$65 lta39 ltb2 ltcb ltdc lte9e ltg6 " +
        "lth25 lti89 ltm6 ltn8 lto6 ltp8 ltr16 lts20 ltt6 ltu6 ltv2 lty8 lu$21 lua24 lub5 luc3 lud44 lue58 lug1e lui11 luj2 luk2 lul7 lum77 " +
        "lun15 luo8 lup7 lur3d lus95 lut1c lux7 luy3 lv$5 lva15 lve2e lvib lvl4 lvo6 lvp2 lwaf lwe3 lwic lwm3 lwo3 lwr2 lx$b ly$261 lya8 " +
        "lyb2 lyd3 lye3 lyi10 lyk2 lyl5 lynb lyo6 lypc lyra lys8 lytd lyx2 lyzc lz$3 lzw2 ma$59 maa8 maba mac7f mad20 mag4b mah9 maida " +
        "majb mak1a mal9c man1b6 map88 marc0 mas61 mat10c mau6 mav2 max83 may14 maz7 mb$17 mba17 mbe72 mbi2b mbl21 mbn6 mbo2b mbp10 mbr8 mbs11 mbuf " +
        "mby2 mc$e mcab mcc5 mcd2 mcf2 mch19 mci3 mcl5 mcm3 mcob mcr3 mcs9 mct3 mcy2 md$21 mda6 mdb6 mdc2 mdd4 mde7 mdf4 mdi19 mdk4 " +
        "mdl12 mdm7 mdn3 mdr2 mds2 mdu2 me$31a mea31 meb5 mec17 medc1 mee25 mef5 meg15 meh9 meib mek4 mel12 mem73 men1de meo4e mep2f meq6 merdd " +
        "mesd8 metd4 meu3 mev2 mewc mexd mez5 mf$9 mfa3 mfe2 mfid mflc mfm3 mfo6 mfp7 mfs3 mft1b mg$8 mga4 mgb6 mge6 mgf2 mgi2 mgm4 " +
        "mgo7 mgr1c mgt3 mgue mha5 mhe3 mhz4 mi$25 mib9 mic43 mid28 mief mig16 mii2 mil41 mimd min198 mip4 miq3 mir1f mis6e mitd0 miv2 mix12 " +
        "miz41 mk$4 mka2 mkc5 mkd3 mke1c mkl2 mkq2 ml$25 mla6 mlda mle4 mlf3 mlid mlj2 mlm3 mln3 mlo7 mls6 mlt2 mly5 mm$20 mma58 mmb2 " +
        "mmcd mmd3 mme50 mmf8 mmg2 mmi2e mmk3 mml4 mmm6 mmo1e mmpa mms12 mmu1a mmy2 mn$d mna7 mne6 mni5 mnl2 mno8 mnr2 mnsa mo$12 moa4 " +
        "mobd moc4 mod118 mof15 moh4 mok2 mol4 mom5 mon9b moob mop4 mor4f mos14 mot55 mou58 mov9b mox2 moz3 mp$60 mpa7a mpc2 mpd2 mpe3c mpf11 " +
        "mph9 mpi1a mpj2 mpl15b mpo88 mpp8 mpr4f mps13 mpt7d mpu5e mpv7 mq$4 mqi2 mr$7 mra5 mrb2 mre15 mrg2 mri4 mrm3 mro8 mrp3 mrt4 mru2 " +
        "ms$9f msa1b msb2 msc16 msd13 mse2e msf3 msg15 msh9 msi30 msk6 msl3 msm6 msn3 mso2 mspc msr5 mssc mst1b msu4 msv8 msw7 msx4 mt$13 " +
        "mta7 mtc2 mte3 mth8 mtia mto4 mtp9 mtr3 mts8 mtua mty6 mu$e mua2 muc6 mud3 muib mukc mul79 mum21 mun1e muoa mup4 mur8 mus16 " +
        "mut24 muu4 muxc mv$8 mva2 mvc2 mvi7 mvs6 mw$2 mwa16 mwe3 mwi5 mwo4 mx$6 mxd2 mxm3 mxs4 my$c mya10 myc7 myd6 myf4 myi2 myl2 " +
        "mym3 myo2 mypb myr6 mysd myt3 myu7 myv2 myx2 mza5 na$a8 naa7 nab97 nac45 nad32 nae2 naf3 nagd3 nah5 nai9 nak2 nal151 nam255 nan2a " +
        "nap4e nar2a nas2d nat10b nau27 nav75 naw3 nax3 nay4 nb$3 nba20 nbe7 nbf2 nbif nbl2 nbo3a nbr9 nbs2 nbt6 nbu1a nbya nc$60 nca5e ncbe " +
        "ncce ncd3 nce1e3 ncf9 nchc2 nci3f nck2 ncl6c ncmb ncoe5 ncp10 ncq3 ncr73 ncs15 nct3b ncu11 ncw2 ncy46 nd$180 ndab3 ndb13 ndcf ndde nde1cb " +
        "ndfb ndg2 ndh5 ndi121 ndla2 ndmb ndn13 ndodb ndp32 ndr20 nds84 ndt35 ndu1f ndv3 ndw12 ne$124 nea1a neba necca nedee nee22 nef8 neg72 neh2 " +
        "nei1a nek7 nel5a nem3 nen46 neo14 nep10 neq5 nerda nesa5 net112 neu8 nev1b new98 nex5e neyd nez4 nf$10 nfa15 nfc7 nff4 nficb nfl22 nfofa " +
        "nfr24 nfs8 nfu6 ng$91a nga43 ngb10 ngc23 ngd16 nge134 ngf10 nggc ngh3 ngi52 ngk10 ngl41 ngm14 ngn6 ngo2d ngp1b ngq2 ngr3b ngs99 ngt42 ngu3d " +
        "ngvd ngw8 ngx8 ngy4 ngz4 nh$c nha32 nhe21 nhie nhod nht17 nhu2 ni$3a nia4d nib4 nic4e nid4c nie28 nif27 nig10 nih4 nii3 nika nil3 " +
        "nim28 nin17b niob nip18 niq17 nis61 nit10f niu5 nivc nix2 niz3d nj$4 nja8 nje12 njia njo12 njr3 njud njy6 nk$6b nkad nkb3 nkd6 nke1d " +
        "nkf3 nkh3 nki16 nkn11 nkoa nks1c nku2 nl$3 nla26 nle17 nli4c nlo6d nls3 nlt6 nlu3 nly4b nma54 nmb2 nmd2 nme2c nmib nmm4 nmo30 nmp5 " +
        "nmt2 nmu2 nn$1e nna6e nne135 nng7 nni35 nnl2 nnm3 nnn1b nno2c nnta nnu4 nnw2 nny3 no$1f noa8 nob9 noc14 nod27 noe11 nof6 nog5 noi10 " +
        "nok4 nolb noma non71 noo11 nop19 nor79 nos33 notf9 nou2a nov10 now45 nox2 noy4 np$e npa35 nped nph6 npi1f npl7 npo1f npr58 nps2 npu28 " +
        "npw4 nqp2 nqt2 nquf nr$5 nra8 nrec0 nri3 nrm5 nro3e nrp5 nrs6 nrt6 nru11 nry6 ns$255 nsa61 nsb3 nsc15 nsd5 nseb3 nsf2a nsg3 nsh23 " +
        "nsid4 nsj3 nsk7 nsl1d nsm21 nsn3 nso38 nsp37 nsq6 nsr6 nssf nst16b nsu5c nsv6 nsw11 nsx3 nsyf nt$3af nta148 ntb10 ntc47 ntd1f nte2b1 ntf39 " +
        "nth3e nti1fb ntj3 ntk2 ntl61 ntm11 ntne nto49 ntp3b ntrdf nts163 ntt33 ntue ntv21 ntw8 nty32 nu$15 nua1d nub3 nud4 nue16 nuf6 nug2 nui7 " +
        "nuk9 nul11 numa2 nun21 nuo9 nup16 nure nus2f nut17 nux5 nv$c nva73 nvd2 nve48 nvi24 nvm2 nvo31 nvp2 nvr2 nw$3 nwa1c nwe7 nwh2 nwi16 " +
        "nwo9 nwr8 nx$5 ny$21 nya1d nyc7 nye8 nyi17 nymb nyn9 nyo11 nyp3 nyt4 nyu9 nyw4 nza8 nze5 nzi7 nzo2 nzu8 nzy6 oa$24 oab2 oac10 " +
        "oadba oal8 oamd oan2 oap3 oar17 oas5 oatf oauc oay2 ob$15 oba2f obb2 obe17 obi18 obj65 obl14 obn2 obod obr4 obs21 obt14 obu2 oby4 " +
        "oc$2b oca10c occ34 ocece ocf2 oche oci2c ock119 ocl10 oco3a ocp4 ocre ocse oct14 ocu1b od$35 odad odb9 odcc odd4 ode147 odh3 odi62 odj4 " +
        "odo7 ods21 odu4f odyf oe$13 oec2 oed6 oeh3 oemb oen15 oer18 oes15 oet7 oex19 of$25 ofa6 ofc3 ofd3 ofe2 off7a ofg2 ofh2 ofi6b ofl8 " +
        "ofm3 ofn2 ofoa ofpa ofr2d ofs4 oft24 ofu2 ofw7 og$56 oga12 ogb2 ogc2 ogdc oge14 ogf21 ogg1e ogh8 ogi37 ogl7 ogmb ogn1d ogo59 ogp6 " +
        "ogr68 ogs1d ogt3 ogu7 ogw9 ogy9 oh$d oha6 ohi9 ohl3 ohm2 ohn3 oho4 oht2 oi$a oic19 oid17 oif2 oind2 oip11 oir2 oisc oiz2 oje4 " +
        "oji5 ojp2 ok$3f oka4 oke88 oki13 okmc oko2 oks6 oku1e oky2 ol$7b ola4a olb7 olc5 old86 ole52 olg3 olicc olk4 olle8 olm2 oln2 olo4a " +
        "olp4 ols21 olt7 olu7f olv2e oly2 om$64 oma8f omb2a omce omd8 ome64 omf5 omi54 oml4 ommaf omn3 omo21 omp21f omr8 oms16 omt5 omub omv4 " +
        "on$7cb onaf0 onb1a onca7 ondb0 one10d onfdc onga3 onh27 onief onj6 onk8 onl88 onm47 onne5 ono36 onp38 onq2 onr42 ons2c1 ont1d9 onu1f onv49 onw11 " +
        "onyd onz2 oo$5a oobe oode ooe2 oofe oog3 ook5c ool43 oomd oon29 oop16 oor1b oos22 oote8 oou8 op$bb opa13 opbf opc5 opd3 ope11c opf3 " +
        "ophe opi42 opj2 opl1b opmf opn5 opo1c opp35 opq2 opr1d ops16 opt7b opu10 opy3a oq$2 or$1e9 ora53 orbe orc78 orde3 ore173 orf1b org2d orh5 " +
        "ori113 orke3 orlf orm11f orn2b oro17 orp5a orr90 orsd9 ort227 oru1d orvf orw2e ory92 os$75 osa11 osb5 osc4 osd7 osec4 osgf oshd osi66 osk5 " +
        "osl6 osm8 osn6 oso1c osp2 osr2 oss4d ost127 osud osv3 osw3 osy2 ot$d8 ota91 otb2 otc1c otd9 oted9 otf15 otg8 oth52 otie1 otk4 otl7 " +
        "otm19 otn3 oto34 otp8 otr13 ots37 ott37 otud otv3 oty3 ou$44 oub1c ouc6 oudf oug29 oui2 oul32 oum3 oun24e oup86 our113 ous64 out1a5 ov$c " +
        "ova1b ovd3 ove1b1 ovib4 ovo7 ovs3 ovx3 ow$c3 owa1d owb3 owc14 owd8 owe8f owf6 owh5 owi39 owk2 owl14 owma ownc9 owoc owp4 owrf ows88 " +
        "owtf owub owv4 oww3 ox$68 oxe4 oxg3 oxif oxs3 oxy32 oy$13 oya6 oyc2 oye9 oyi3 oymb oyo4 oyr3 oz$2 oza3 ozed pa$23 paa6 pab19 " +
        "pac11b pad42 pae2 pag90 pah3 pai2f pakc pal31 pamb pan66 pap22 paq5 par194 pas85 patfa pau37 pav3 paw6 pay18 paz4 pb$7 pba13 pbfb pbk4 " +
        "pbl2 pbob pby3 pc$35 pca7 pcb3 pce3 pch1c pci8 pcl7 pcm4 pcn3 pco23 pcp6 pcr14 pcs7 pct2 pcu3 pd$e pda75 pdb3 pdc12 pde6 pdh5 " +
        "pdie pdl2 pdm7 pdo7 pdp3 pdt2 pdu6 pe$121 pea30 peb3 pecc0 ped62 pee2e pef5 peg4 peh9 pei7 pel10 pem3 pen126 peo9 pep8 per291 pes2f " +
        "pete pew2 pf$9 pfac pfi8 pfn4 pfob pfs15 pfu4 pfxc pg$4 pgr20 ph$36 pha58 phe24 phi3c pho33 phr13 phs7 pht5 phu6 phy16 pi$4c pia6 " +
        "pic3c pid2e pie32 pif2 pig4 pii9 pik6 pil26 pim9 pina3 pio3 pip21 piq2 pir33 pis12 pit11 piu2 piw2 pix5 pje2 pju2 pkc4 pke10 pkg9 " +
        "pki3 pkt4 pku2 pl$14 pla109 pld3 ple16e plg5 pli120 plmd plo2d plu28 ply36 pm$22 pma20 pmc4 pmed pmi2 pmk7 pmm3 pmoc pmp4 pms6 pmva " +
        "pn$25 pna8 pnd3 pne3 pnp10 pnr2 pns4 pnu6 po$12 poa3 pob2 poc5 pod3 poe5 pof3 pog9 poi71 pok7 polb9 poma pon72 poo34 pop23 por1b6 " +
        "posc4 pot14 pou6 pow2b pp$30 ppa16 ppb5 ppc11 ppd6 ppe85 pph9 ppi36 pplc6 ppm10 ppn2 ppo5f pppb ppr37 pps10 ppta ppu7 ppxe ppy5 pq$3 " +
        "pqc2 pqm2 pqr2 pqu4 pr$a pra18 prb2 pre265 pri101 prl3 prm9 pro3b8 prr2 prs3 prtb pru9 ps$92 psa5 psc2 pse53 psf2 psh1a psi1c pske " +
        "psm13 psr2 pss5 pst17 psu9 psv3 psy3 pt$86 pta1a ptb2 ptc8 ptd2 pte4f ptf5 pthb pti11d ptl8 ptn2 pto42 ptpd ptr18 pts25 ptu29 pty17 " +
        "pu$e pua4 pub42 puc3 pud5 pue4 puf2 puga pui2 puk5 pul1e pumb pun14 puo3 pup9 pur33 pus1f putb8 pv$1e pve5 pvi4 pw$6 pwa16 pwd8 " +
        "pwe3 pwi5 pwl5 pwo7 pwr7 px$f pxa2 pxb2 pxd3 pxm2 pxs2 pxu2 py$28 pya2 pyc2 pyd2 pyeb pyf3 pyi5 pyo4 pyp2 pyrd pyt2 pz$2 " +
        "qa$6 qaa5 qad4 qaf5 qal2 qam2 qap6 qar3 qat4 qc$6 qco2 qde2 qdnd qe$3 qef2 qer3 qfa4 qha2 qhe2 qhw5 qi$4 qid3 qie4 qii2 " +
        "qin2 qit3 ql$d qla2 qlb4 qlc3 qld4 qle3 qlf3 qli8 qlm3 qln3 qls6 qlt2 qlv2 qm$6 qma3 qmf3 qmi2 qmo2 qmp10 qmsf qn$3 qo$2 " +
        "qof2 qoo2 qos6 qot2 qp$3 qpc10 qpe3 qpr8 qrs2 qs$3 qse3 qsi2 qsu4 qth7 qto2 qtr2 qu$4 qua5e qud2 que224 quida quo3b qur2 qus6 " +
        "quu3 qwa3 qwe2 qwi3 qwo3 qx$2 qya2 qye2 qyo2 qyr2 qyz2 ra$73 raaa rab3a rac106 rad5b raea raf10 rag64 rah9 rai5a raj4 rak6 ral61 " +
        "ram107 ran19f rap66 raq3 rar2c ras5b rat229 rau9 rav14 raw1c rax3 ray11 raz5 rb$13 rba23 rbed rbi1a rbk2 rbl6 rbo11 rbp4 rbq2 rbr2 rbsf " +
        "rbt4 rbue rc$1b rca1f rce13b rcfd rch5a rci10 rcl25 rcm7 rco39 rcp4 rcrd rcs5 rct4 rcu19 rcv3 rd$c2 rda21 rdbd rdc7 rdd5 rde56 rdf3 " +
        "rdh3 rdi4d rdl10 rdm7 rdn11 rdo8 rdpc rdrd rds54 rdt9 rdu8 rdwf re$201 rea30f reb27 rec288 red259 reed0 reff3 regf8 reh13 rei3d rej17 rek9 " +
        "rela4 rem112 ren19a reob rep144 req13a rer52 res4f5 retf7 reu15 rev99 rew2c rex19 rey3 rf$1b rfa4e rfb4 rfc6 rfe8 rff2 rfi2b rfl15 rfm7 rfn2 " +
        "rfo3c rfq2 rfrd rfs3 rft4 rfu3 rfw2 rg$14 rga17 rgc2 rged8 rgh5 rgi16 rgl2 rgo9 rgr15 rgs7 rgu11 rgye rha6 rhe9 rhi5 rhof rhs2 " +
        "ri$55 riab1 rib61 ric95 rid57 ried8 rif49 rig89 rii5 rikd ril14 rim43 rin199 rio5e ripaf riq2 rir3 ris5f rit18d riu4 rivb4 rix4 riy8 riz2b " +
        "rje4 rk$6d rka7 rkb4 rkc7 rkd3 rke40 rkg4 rkh6 rki2b rkl7 rkm7 rkn2 rko2 rkp2 rkq1c rks31 rkt5 rku4 rkv3 rl$2a rla21 rlc4 rld3 " +
        "rle10 rlf6 rli2f rll2 rlm3 rln2 rlo1e rlr4 rlsa rly23 rm$74 rmad5 rmc2 rmd5 rme38 rmf3 rmg2 rmi99 rmm6 rmn2 rmo17 rmp3 rmr9 rms1e " +
        "rmt5 rmuc rmwb rmx3 rn$53 rnadb rnc6 rne50 rnf9 rng2 rnh3 rni34 rnl2 rno12 rns19 rnt6 rny2 ro$31 roa27 rob34 roce0 rod37 roe9 rof64 " +
        "rog41 roh4 roi5 roj3 rok2d rolc1 rom7e ron87 roo48 ropb4 rorc3 ros56 rot93 rou157 rovaa row58 rox42 roy10 roz9 rp$28 rpa16 rpbd rpc20 rpd7 " +
        "rpe9 rpf3 rph14 rpi4 rpl15 rpm2 rpn5 rpo26 rpr34 rps6 rptb rq$3 rql4 rquf rr$32 rra26 rrb2 rrd3 rre131 rri47 rrl3 rrof2 rrp2 rrs3 " +
        "rru42 rry12 rs$1fb rsa2a rsb16 rsc12 rsd5 rse9b rsh46 rsicd rsk8 rsl58 rsn3 rso33 rsp15 rss5 rst44 rsu6 rsv4 rsyb rt$18b rta6c rtba rtc27 " +
        "rtde rtea8 rtf23 rtg3 rth3a rti11f rtk5 rtl14 rtm22 rtn27 rto27 rtp17 rtr2f rts70 rtt24 rtu50 rtv3 rtw5 rtx3 rty4f rtz4 ru$2d rua4 rub15 " +
        "ruc41 rud2 ruea rug5 ruk2 rul34 rum2a runb8 ruo5 rup45 rur6 rus68 rut7 ruu2 rux4 ruz3 rv$22 rva29 rvc5 rvece rvi9f rvl2 rvo6 rvs4 " +
        "rw$2 rwa3e rwe6 rwi13 rwo4 rwr12 rx$3e ry$17a rya14 ryba ryca ryda ryeb ryf17 ryg2 ryh9 ryi1f ryj2 ryk6 ryl8 rymb rync ryo9 ryp84 " +
        "ryq3 ryr12 rys1d ryt1b ryu9 ryva ryx4 rz$2 rze4 sa$52 saab sab7e sac60 sad1d saea saf11 sag59 sah3 sai10 sakd sal50 sam77 san52 sao2 " +
        "sap1d saq2 sar27 sas29 sat42 saud sav45 saw4 sax2 sayc saz2 sb$5 sbab sbe5 sbi2 sbo4 sbr5 sbt13 sbu8 sby6 sc$30 sca7c scb2 scc3 " +
        "scd4 sce37 sch79 sci1a scl4 scmc sco92 scp9 scrf8 scs20 scte scu12 sd$14 sdac sdb8 sdc3 sdda sde11 sdh2 sdi1b sdk4 sdm2 sdn7 sdo4 " +
        "sdp3 sdr4 sdsf sdt6 se$1cd sea40 sebb sec148 sedef see34 sef16 seg13 seh5 sei12 sek3 sel65 sem5a sen12d seo23 sep36 seq29 ser2a4 ses170 set218 " +
        "seu8 sev16 sew6 sex11 sey8 sfa6 sfc4 sfe10 sfi15 sfl5 sfm3 sfo25 sfp2 sfq2 sfr4 sft3 sfu31 sfy5 sg$16 sga4 sgc5 sge1b sgi2 sgl2 " +
        "sgm4 sgr4 sgs3 sgu2 sh$c8 shae6 shc9 shdc she9c shf5 shg2 shh6 shi58 shk8 shm18 shn7 shode shq3 shr19 shs7 sht19 shu36 shv5 shw11 " +
        "shy8 si$37 sia37 sib66 sic3c sidc1 sie11 sif1d sig9a sih2 sii4 sik5 sil2d sim41 sin105 sio1f2 sip7 sir8 sis7c sitb3 siv39 six1b siza4 sje2 " +
        "sjf3 sjo3 sk$de ska14 skb2 skc2d skd4 ske17 skf8 skg5 skh1b ski6b skk4 skl4 skm10 skn7 skod skp15 skr23 sks3b skt11 skuc skv3 skwb " +
        "sky3 sl$15 sla55 slcf sle12 slf3 slh2 sli20 slo34 slr54 slt3 slv3 slyf sm$25 sma52 smb9 sme13 smg4 smi30 smkb sml4 smm3 smo1f smq3 " +
        "smre smsf smt9 smu3 smx3 sn$2d sna34 snd3 sne6 sni6 snj5 snm3 snoe snp12 snse snt5 so$27 soa3 sob6 soc52 sod3 sof2c sog4 soh4 " +
        "sol77 som1c son77 soo7 sopa sor75 sos2 sot9 soudb sov6 sox2 sp$3a spa87 spdd spe103 sph12 spi23 spl66 spm3 spn10 spoa4 spp4 spr1a sps5 " +
        "spu4 spx3 sq$4 sql31 sqm5 sqp4 sqs3 squ19 sr$15 srad src1a sre28 srf3 srg2 sri3 srk5 srm12 sro3 srta sru17 srv1a ss$182 ssa93 ssb7 " +
        "sscf ssd8 sseeb ssf31 ssg2 ssh7 ssi208 ssk2 ssl25 ssm15 ssn13 sso61 ssp1d ssr6 sss21 sst31 ssu56 ssv7 ssw37 ssy10 st$22c sta3eb stbd stc1e " +
        "std1f ste22d stf18 stg4 sth20 sti132 stj3 stlf stm35 stn1c sto15b stp33 stq3 str21a sts72 stt14 stu17 stv9 stw6 stx5 sty17 su$14 sua18 sub164 " +
        "suc59 sud5 sue27 suf1f sug3 sui13 suk6 sul55 sum81 sun15 suo3 sup9a sur62 sus3b sut16 sv$f sva14 svc1a svd2 sve3 svf2 svi3 svn2 svo7 " +
        "svr8 svs2 sw$4 swa36 swc4 swd2 swe17 swi3a swo3d swp5 swr2 sxf2 sxm2 sxs9 sy$e sya5 syc3 syd4 sye3 syl16 sym24 synff syo6 syp2 " +
        "syrd sysa9 syt4 syx2 syy2 sz$6 sza2 sze2 szw2 ta$f2 taaa tabd5 tacca tad38 taea taf14 tag92 tah8 taib1 taj7 tak2c tal146 tam31 tan123 " +
        "tao5 tap29 taq2 tar171 tas189 tat1fb tau20 tav17 tawc taxc tay9 tb$9 tbaf tbc3 tbe15 tbg2 tbi1a tbl6 tbo1f tbr6 tbs4 tbt2 tbue tby4 " +
        "tc$24 tca3d tcb3 tcc2 tcd2 tce20 tcf8 tcga tch119 tci3 tcl32 tcm7 tcn4 tco84 tcp16 tcr30 tcs2 tct9 tcu21 td$18 tda20 tdb4 tdc15 tde46 " +
        "tdg2 tdh2 tdi2c tdl4 tdm3 tdna tdo38 tdp2 tdr6 tds9 tdt2 tdu3 te$331 tea64 teb22 tecd9 ted487 tee2e tef1a teg49 teh18 tei21 tej5 tek7 " +
        "tel6a tem137 ten1b3 teob tep3f teq6 ter503 tes1c5 tet56 teub tev27 tew1c tex8e tez2 tf$5 tfa11 tfd4 tfi69 tfl16 tfm2 tfo3b tfq3 tfr12 tfs26 " +
        "tftd tfuc tg$2 tga4 tge2f tgi2 tgm2 tgo7 tgp2 tgrb tgt7 tgu9 th$125 tha7f thb4 thcb thdd the1c0 thf9 thh4 thi93 thl3 thm21 thn13 " +
        "thoab thp8 thr81 ths1f tht16 thu37 thv4 thwe thy8 thza ti$3b tia131 tib2f tic17e tid53 tie5d tifec tig18 tih2 tii3 tij2 tikd til91 tim1c7 " +
        "tin350 tio978 tip36 tir16 tis52 tit6a tiu5 tiv1c6 tizb tje2 tjoa tk$2 tka4 tke19 tki2 tl$2a tla1d tlb5 tld7 tle33 tlf3 tlhb tli20 tlm17 " +
        "tlo66 tlr3 tls18 tlu3 tlv3 tly5d tm$1b tma5e tmc2 tme50 tmf8 tmgd tmie tml8 tmm4 tmn2 tmo2f tmp5 tmr3 tms4 tmt3 tmu7 tn$7 tna60 " +
        "tne37 tno8 tnt4 tnua to$72 toae tob1a toc39 tod1b toe1a tof15 tog1f toh4 toif tok36 tol1e tom64 ton58 too38 topbc tor254 tos30 tot49 tou1d " +
        "tov12 towb tp$41 tpa4a tpcf tpee tpf3 tpg8 tpia tpm23 tpo54 tpp2 tpq4 tpr85 tps1d tpta tpu2d tq$2 tqp3 tqud tr$37 tra22e trb3 trc5 " +
        "trd7 tre185 tri197 trk3 trl9 trn3 troaa trs2 trt2 trub4 try74 ts$321 tsa24 tsc1f tsda tse7a tsf4 tsh2b tsi3a tskf tsl2 tsn4 tso2e tsp12 " +
        "tsq3 tsr7 tsse tst82 tsu26 tsv9 tswd tsy13 tt$c ttaa0 ttd3 ttee3 ttg2 tth27 ttiad ttl1b tto32 ttp3a ttr63 ttsa ttt3 ttu7 tty1e tu$1c " +
        "tua42 tub4 tud12 tuec tug9 tuk2 tul3 tumd tun43 tuo4 tup3f turf3 tus53 tut21 tuv4 tux4 tva10 tvc2 tvd2 tve17 tvi21 tvl4 tvof tvr2 " +
        "tvs6 tvw3 tw$9 twa25 twc2 twe1a twi12 twk2 two5a twq2 twr6 tws2 twv2 tx$18 txa3 txf10 txg4 txi3 txn4 txo2 txs2 txt6 ty$14e tya2 " +
        "tyb2 tyc15 tyd10 tye4 tyf5 tyh2 tyi10 tyl10 tym3 tyn4 tyo3 typ131 tyq2 tyr2 tys5 tyt4 tyw2 tz$8 tza5 tze8 tzi2 tzo6 tzr2 tzub " +
        "tzv2 ua$18 uab2 uac4 uad12 uae3 uagd uah2 ual86 uam3 uan18 uar34 uas4 uat49 uay5 ub$27 ubac ubbc ubc13 ubde ube6 ubf4 ubg4 ubid " +
        "ubj3b ubk9 ubl66 ubm21 ubn11 ubod ubp4 ubr2 ubs72 ubt1a ubu3 uc$11 uca7 ucc4f uce27 uch1b uci6 uckc ucl4 uct63 ud$11 uda1d udc5 udd4 " +
        "ude53 udf5 udh2 udi59 udo5 udpe udr6 uds8 udy6 ue$9c uea5 uec8 ued29 uee5 uef4 ueh4 uei15 uek4 uelc uem4 uen45 uepa uer9f ues108 " +
        "uet6 ueu71 uf$5 ufa7 uff6b ufo2 ufs5 ug$16 ugad ugb2 ugc6 uge6 ugg1d ugh2e ugi15 ugmb ugp4 ugrc ugsb ugt6 uguf uh$d uhi4 uhu2 " +
        "ui$28 uiaa uic3a uid50 uie16 uig5 uil2f uin1a uip3 uir64 uis18 uit23 uiv7 uja8 uk$f uka4 ukb2 ukh10 uki2 uko2 ukr6 ukt6 ukub uky2 " +
        "ul$4f ula81 ulc2 uld31 ule73 ulf5 ulg9 ulie ulk4 ull80 uln5 ulo4 ulp6 uls2 ult125 ulua uly4 um$76 uma1a umb54 umc8 umd7 ume10d umf9 " +
        "umh2 umi14 uml2 umm10 umn1b umoe ump34 umq2 umsf umt3 umu7 umv2 umw2 un$4a una76 unbd uncc2 und177 une2d unf10 ung35 unh9 unia8 unja " +
        "unk21 unl3a unm23 unn53 unoa unp21 unr46 uns4c unt162 unu14 unwd uo$28 uoi2 uon4 uop1f uor7 uot3a uouf uox25 uoy3 up$105 upa8 upb3 upcc " +
        "upd7f upe2c upf8 upg17 upha upi13 upk10 upl30 upm10 upn11 upo8 upp75 upr7 ups2e upt3f upw9 upy3 uq$2 ur$61 ura67 urb2 urcc5 urde ure16a " +
        "urf8 urg26 urh3 uri94 urk10 url20 urm7 urn5a uro13 urpc urq2 urr89 urs34 urtf uru13 urv17 urx22 us$e7 usa36 usb6 usc7 use189 ush52 usi48 " +
        "uslf usn10 uso2 usp32 usr3 uss12 ustca usuc usv5 usw2 usy7 ut$fd uta28 utb17 utc18 utd30 utef5 utf1e utg6 uthff uticd utlb utm4 uto9d " +
        "utp2f utr9 uts34 utt1f utu12 utv3 utw4 uty2 utz2 uu$c uue2 uui9 uur2 uus3 uuy3 uv$5 uva6 uve2 uvs4 uvw2 uwa6 ux$3a uxe5 uxi3 " +
        "uxr3 uxs4 uy$3 uya8 uyg2 uyi2 uyu2 uz$3 uzb6 uze2 uzz3 va$14 vaa2 vabf vacb vad3 vagb vai35 vak5 val15c vam4 van25 vap3 var46 " +
        "vas9 vat102 vau14 vav4 vb$3 vbc2 vbs3 vbu2 vc$15 vca2 vcc2 vcm2 vcnc vco4 vcp4 vcs7 vd$a vdia vdl2 vdm3 vdo3 vdp2 vdr4 vds7 " +
        "vdv2 ve$16a vea7 veb3 vec25 ved9e veec vefa veh6 veid vel7c vema ven119 veob vep15 ver37b ves5d vet18 veu5 vev4 vewa vex4 vey3 vfa4 " +
        "vfi7 vfl2 vfp2 vfr2 vfs2 vga3 vgf3 vgv3 vhd8 vi$a via1c vicf4 vidab vie90 vig66 vii2 vik5 vil26 vin52 vio32 vir3e visa6 vit23 viy9 " +
        "vl$3 vlac vlc2 vlf2 vlo3 vls2 vm$6 vme2 vmo3 vmr3 vms4 vn$2 vno2 vo$9 vob2 voc1c voi2b vok26 vol74 von2 vor3 vos6 vot4 vow9 " +
        "vp$5 vpa5 vpn5 vr$8 vra4 vre4 vri2 vrs4 vs$a vscb vsi3 vsk5 vsm8 vsp3 vssf vswc vsyc vtaa vth4 vtl5 vto2 vts2 vtu5 vtv2 " +
        "vtx3 vu$5 vul9 vun3 vur2 vut2 vwa2 vwr3 vwx2 vxm3 vy$c vyk2 wa$4a waa24 wab6 wacc wada wae4 wag7 wah6 wai3f waj2 wak19 wal46 " +
        "wam3 wan36 wap1d waq2 warb9 was1a wat1d wau3 wav11 waw4 way47 waz4 wb$2 wba7 wbe3 wbo3 wbx3 wc$3 wce6 wcf3 wch5 wcl2 wcn4 wco17 " +
        "wcr3 wd$d wda5 wde7 wdf2 wdi7 wdm3 wdr3 we$40 wea13 webe wec6 wed4e wee26 weg3 weid wel1b wen8 weo3 wep3 wer66 wes15 wet3 wev11 " +
        "wex4 wf$5 wfa2 wfc3 wfd3 wfi3 wfl2 wfo2 wfp19 wfq2 wfw2 wg$5 wha8 whe28 whh2 whi1b who15 whq3 why3 wi$24 wia2 wic8 wid3e wifa " +
        "wig7 wii15 wil1a wime win13c wipd wir13 wis20 wit67 wiz9 wk$2 wke4 wks3 wla10 wle12 wli9 wlo3 wly4 wm$d wma9 wme8 wmi6 wmm2 wmo5 " +
        "wmp6 wmt3 wmv2 wn$62 wna7 wnc4 wnd7 wne20 wnff wngc wnh2 wnic wnl1a wno3 wnp8 wnr2 wns5 wnw7 wo$1f wob2 wof4 wok4 wol9 won8 " +
        "woo19 wop4 wor13c woue wow3 woy2 wp$6 wpa10 wpe2 wpma wpo2 wpp3 wprf wps2 wpt2 wpu2 wql2 wr$4 wraa wre15 wri8b wroc wrp2 wrs2 " +
        "ws$5d wsa10 wsd3 wse1f wsf2 wsh7 wsi11 wsl8 wsm2 wso2 wspa wsr4 wss3 wste wsu3 wte3 wth7 wti9 wtr3 wty3 wu$5 wul2 wunb wuo3 " +
        "wus8 wva4 wve4 wvi5 ww$6 wwa9 wwi3 wwn3 wwo3 www7 wx$2 wxy2 wyn2 wyo2 wz$6 xa$9 xaa2 xab3 xacd xad6 xaga xal2 xam20 xan2 " +
        "xap7 xar2 xas3 xat7 xau5 xbf4 xbl2 xboc xbu2 xc$4 xca4 xce2a xch10 xcl35 xcoa xcp8 xcy2 xd$3 xda3 xde8 xdf3 xdv2 xe$e xeb2 " +
        "xec62 xed1e xee2 xeh2 xel4 xem24 xend xep2 xer5 xes10 xev4 xew4 xex6 xf$9 xfa8 xffa xfi7 xfk2 xfl3 xfrb xg$2 xgi8 xgu4 xh$3 " +
        "xha9 xho3 xi$d xia5 xib2 xic5 xid2 xiea xif2 xil3 xim1b xinc xip4 xis3c xit37 xix2 xjf3 xke2 xla7 xle6 xlo4 xmi2 xml22 xms2 " +
        "xnr2 xnw3 xo$4 xob3 xof5 xon6 xop3 xor3 xot2 xou2 xox2 xp$6 xpa27 xpe30 xpi2a xpl15 xpo33 xpr13 xpu2 xpw2 xqu2 xra5 xre13 xrt2 " +
        "xs$7 xsa3 xsed xsie xsl3 xsm3 xso2 xst7 xsu5 xsy2 xt$66 xta3 xtb2 xtc3 xtd4 xte8e xthd xti4 xtl3 xtp5 xtr1b xts13 xtub xtv4 " +
        "xty3 xu$2 xuo7 xup4 xus2 xva5 xve3 xwa7 xwe4 xwi2 xwo2 xwr7 xx$16 xxx5a xy$21 xya2 xyb2 xye2 xyf3 xyh2 xyi7 xys4 xyz4 ya$3b " +
        "yaab yab6 yacd yada yae3 yag5 yah9 yaj4 yake yal19 yam5 yan5c yap9 yaq2 yara yas6 yat9 yau7 yaw3 yay5 yaz5 yb$2 ybae ybe4 " +
        "ybi5 ybl3 ybof ybr6 yby4 ycac ych1c ycl27 ycm2 yco1c ycr3 ycu2 yd$4 yda6 yde12 ydi8 ydn7 ydod ydr19 ydw2 ye$e yea8 yed27 yee8 " +
        "yehb yek7 yel5 yem2 yend yeo1d yer4d yesd yet9 yev7 yex10 yf$2 yfa3 yfi18 yfld yfo3 yfq3 yfr8 yge4 ygh2 ygl3 ygu2 yha7 yhe5 " +
        "yhi6 yho3 yi$12 yia2 yid18 yief yif2 yig3 yik2 yil2 yim4 yin8a yip5 yis9 yit4 yiw2 yix2 yjo3 yka2 yke6 ykj2 ykn2 yla4 yle16 " +
        "yli22 yll11 ylo20 ym$3 yma10 ymb11 yme14 ymi5 yml6 ymm9 ymof ymp3 yms4 yn$9 yna37 ynce7 yne9 yng4 yni2 ynn2 yno6 ynr2 ynt13 ynu2 " +
        "yo$10 yoa2 yob3 yod3 yof2 yog2 yon12 yoo6 yop8 yora yot8 you55 yox2 yp$1d ypa25 ype145 yphd ypib ypo19 ypr26 ypt74 yqu5 yr$1c yra6 " +
        "yre20 yrg4 yri1b yru2 yrx15 ys$59 ysa5 ysc5 ysd5 yse2a ysh6 ysi22 ysk3 ysle ysn2 yso9 ysp9 ysr3 yss6 yst8f ysu4 ysv5 ysy6 yt$1e " +
        "yta19 yte5a yth6 yti17 ytl2 ytob ytr6 ytt2 yty6 yu$d yud3 yuk3 yun5 yuo7 yup5 yur7 yus12 yut4 yuv6 yux2 yv$2 yvaa yvo4 yvs2 " +
        "ywaa ywe2 ywh2 ywi6 ywo15 yx$1c yy$d yya4 yyr2 yyy10 yyz3 yz$5 yze8 yzi4 yzs2 yzy2 yzz3 za$17 zaa6 zab6 zag4 zah3 zai4 zak5 " +
        "zam5 zan5 zap4 zaq2 zara zat5f zax3 zay2 zb$3 zbe6 zco3 ze$de zea4 zec5 zed5c zee7 zef2 zeg3 zeh3 zei8 zel7 zemb zen12 zeo4 " +
        "zep4 zeq2 zer32 zes20 zet8 zew2 zex3 zfa3 zg$2 zh$9 zhaa zhee zhi9 zhoa zhua zhw3 zhy5 zi$b zieb zig6 zil6 zim3 zin2d zip4 " +
        "zit3 zix3 zje3 zla3 zle2 zli3 zlo2 zo$8 zoa2 zod3 zom2 zon1e zoo3 zop3 zor2 zot2 zox3 zqa2 zre2 zro2 zsk2 zsp2 zsq2 zst4 " +
        "zsu3 zsy2 ztd2 zth5 zu$b zud2 zue3 zul3 zun2 zuo5 zup4 zurf zus3 zutb zux3 zve2 zw$3 zwa2 zwe3 zwg2 zy$6 zyl2 zyo2 zyp3 " +
        "zyr6 zyt3 zyx3 zz$7 zza8 zze6 zzic zzl3 zzo4 zzu6 zzy6 zzz2 ";
}
