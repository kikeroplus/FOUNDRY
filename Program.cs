// FOUNDRY の窓。720x960 の絵 (世界 240x320 の 3 倍) を出す。窓の大きさを変えても縦横比は保つ。
// 1 秒 30 回ゲームを進め、1 秒 60 回描く (動くもののあいだを補ってなめらかに見せる)。
// キーボードと XInput のゲームパッド、ふつうの USB ゲームパッド (SkyLark Joystick など) で操作する。
// 設定とランキングは %APPDATA%\Foundry\ に、1 つにつき小さなテキストファイル 1 つで残す
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Foundry
{
  static class Store
  {
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Foundry");

    public static string Read(string name)
    {
      try
      {
        string p = Path.Combine(Dir, name);
        return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
      }
      catch (Exception)
      {
        return null;
      }
    }
    public static void Write(string name, string text)
    {
      try
      {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, name), text, Encoding.UTF8);
      }
      catch (Exception)
      {
      }
    }
  }

  // XInput のゲームパッド (Xbox 系)。つながっていないときは 2 秒ごとに探しなおす
  static class Pad
  {
    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
      public uint Packet;
      public ushort Buttons;
      public byte LT, RT;
      public short LX, LY, RX, RY;
    }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int Get14(int i, out State s);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int Get910(int i, out State s);
    public const int UP = 0x1, DOWN = 0x2, LEFT = 0x4, RIGHT = 0x8, START = 0x10, BACK = 0x20, LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    static int api = 0; // 0 = 1_4、1 = 9_1_0、2 = 使えない
    static int index = -1;
    static long nextScan;

    static bool Try(int i, out State s)
    {
      s = new State();
      try
      {
        if (api == 0)
          return Get14(i, out s) == 0;
        if (api == 1)
          return Get910(i, out s) == 0;
      }
      catch (Exception)
      {
        api++;
      }
      return false;
    }

    public static bool Poll(out State s)
    {
      s = new State();
      if (api >= 2)
        return false;
      if (index >= 0)
      {
        if (Try(index, out s))
          return true;
        index = -1;
      }
      long now = Environment.TickCount;
      if (now < nextScan)
        return false;
      nextScan = now + 2000;
      for (int i = 0; i < 4; i++)
        if (Try(i, out s))
        {
          index = i;
          return true;
        }
      return false;
    }
  }

  // XInput でない USB ゲームパッド (SkyLark Joystick など。Windows の「ゲームコントローラー」に出るもの) を winmm で読む。
  // Xbox 系 (マイクロソフトの機器) は XInput で読むので除く。つながっていないときは 2 秒ごとに探しなおす
  static class Joy
  {
    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
      public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Caps
    {
      public ushort Mid, Pid;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
      public uint Xmin, Xmax, Ymin, Ymax, Zmin, Zmax, NumButtons, PeriodMin, PeriodMax, Rmin, Rmax, Umin, Umax, Vmin, Vmax, CapsFlags, MaxAxes, NumAxes, MaxButtons;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxd;
    }
    [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref Info info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "joyGetDevCapsW")] static extern int joyGetDevCaps(IntPtr id, ref Caps caps, int size);
    [DllImport("winmm.dll")] static extern int joyGetNumDevs();
    const uint JOY_RETURNALL = 0xFF;
    const ushort MICROSOFT = 0x045E;

    static bool broken;
    static int id = -1;
    static uint xmin, xmax, ymin, ymax;
    static long nextScan;

    static bool Read(int i, out Info info)
    {
      info = new Info();
      info.Size = (uint)Marshal.SizeOf(typeof(Info));
      info.Flags = JOY_RETURNALL;
      return joyGetPosEx(i, ref info) == 0;
    }

    // -1〜1 (遊びを抜いたもの)
    static float Axis(uint v, uint lo, uint hi)
    {
      if (hi <= lo)
        return 0;
      float n = (v - (float)lo) / (hi - lo) * 2 - 1;
      const float dead = 0.15f;
      float a = Math.Abs(n);
      return a < dead ? 0 : Math.Sign(n) * Math.Min(1, (a - dead) / (1 - dead));
    }

    // ax・ay は右・下が正。buttons はボタン 1 が 0x1。pov は十字 (Pad の UP・DOWN・LEFT・RIGHT と同じビット)
    public static bool Poll(out float ax, out float ay, out uint buttons, out int pov)
    {
      ax = ay = 0;
      buttons = 0;
      pov = 0;
      if (broken)
        return false;
      try
      {
        Info info = new Info();
        if (id >= 0 && !Read(id, out info))
          id = -1;
        if (id < 0)
        {
          long now = Environment.TickCount;
          if (now < nextScan)
            return false;
          nextScan = now + 2000;
          int n = Math.Min(16, joyGetNumDevs());
          for (int i = 0; i < n && id < 0; i++)
          {
            Caps c = new Caps();
            if (joyGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf(typeof(Caps))) != 0 || c.Mid == MICROSOFT)
              continue;
            Info t;
            if (!Read(i, out t))
              continue;
            id = i;
            xmin = c.Xmin;
            xmax = c.Xmax;
            ymin = c.Ymin;
            ymax = c.Ymax;
          }
          if (id < 0 || !Read(id, out info))
            return false;
        }
        ax = Axis(info.X, xmin, xmax);
        ay = Axis(info.Y, ymin, ymax);
        buttons = info.Buttons;
        if (info.Pov <= 36000)
        {
          int d = (int)info.Pov;
          if (d >= 31500 || d <= 4500) pov |= Pad.UP;
          if (d >= 4500 && d <= 13500) pov |= Pad.RIGHT;
          if (d >= 13500 && d <= 22500) pov |= Pad.DOWN;
          if (d >= 22500 && d <= 31500) pov |= Pad.LEFT;
        }
        return true;
      }
      catch (Exception)
      {
        broken = true;
        return false;
      }
    }
  }

  class MainForm : Form
  {
    readonly Game game = new Game();
    readonly int[] screen = new int[D.PW * D.PH];
    readonly Sound snd;
    readonly bool[] keys = new bool[256];
    // 押した瞬間 (次のフレームまで覚えておく。短く叩いても取りこぼさない)
    bool fireHit, startHit, pauseHit, quitHit, backHit, bombHit, upHit, downHit, leftHit, rightHit;
    char typed;
    ushort padPrev;
    uint joyPrev;
    int joyDirPrev;
    bool fullscreen;
    Rectangle normalBounds;
    string osd;
    int osdT;

    readonly Stopwatch clock = Stopwatch.StartNew();
    double nextStep, nextFrame;
    const double STEP_MS = 1000.0 / Game.FPS; // ゲームは 1 秒 30 回進める
    const double FRAME_MS = 1000.0 / 60;      // 描くのは 1 秒 60 回 (あいだを補う)

    public MainForm()
    {
      Text = "FOUNDRY";
      BackColor = Color.Black;
      KeyPreview = true;
      StartPosition = FormStartPosition.Manual;
      MinimumSize = new Size(260, 360);
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
      Icon = MakeIcon(32);

      snd = new Sound();
      game.Snd = snd;
      string s = Store.Read("sound.txt");
      if (s != null)
      {
        string[] a = s.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        float v;
        if (a.Length >= 1 && float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
          snd.Master = Math.Max(0, Math.Min(1, v));
        if (a.Length >= 2)
          snd.Muted = a[1] == "off";
      }
      LoadScores();
      game.TableChanged = SaveScores;
      game.Begin();
      PlaceWindow();
    }

    // ---- 設定 ----
    void LoadScores()
    {
      string s = Store.Read("scores.txt");
      if (s != null)
      {
        string[] lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length && i < 5; i++)
        {
          string[] a = lines[i].Split(new[] { ' ' }, 2);
          int sc;
          if (a.Length == 2 && int.TryParse(a[0], out sc))
            game.Table[i] = new Rank(sc, a[1].Length > 3 ? a[1].Substring(0, 3) : a[1]);
        }
      }
      string n = Store.Read("name.txt");
      if (n != null && n.Trim().Length > 0)
        game.LastName = n.Trim('\r', '\n').PadRight(3).Substring(0, 3);
    }
    void SaveScores()
    {
      StringBuilder sb = new StringBuilder();
      foreach (Rank r in game.Table)
        sb.Append(r.Score).Append(' ').Append(r.Name).Append("\r\n");
      Store.Write("scores.txt", sb.ToString());
      Store.Write("name.txt", game.LastName);
    }
    void SaveSound()
    {
      Store.Write("sound.txt", snd.Master.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + (snd.Muted ? "off" : "on"));
    }

    // 既定は中身が 720x960。画面に入りきらないときは、縦横比を保って縮める
    void PlaceWindow()
    {
      Rectangle wa = Screen.PrimaryScreen.WorkingArea;
      Size frame = SizeFromClientSize(new Size(D.PW, D.PH)) - new Size(D.PW, D.PH);
      int ch = Math.Min(D.PH, wa.Height - frame.Height), cw = ch * D.PW / D.PH;
      Rectangle def = new Rectangle(wa.X + (wa.Width - cw - frame.Width) / 2, wa.Y + (wa.Height - ch - frame.Height) / 2, cw + frame.Width, ch + frame.Height);
      Rectangle r = def;
      bool fs = false;
      string s = Store.Read("window.txt");
      if (s != null)
      {
        string[] a = s.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int x, y, w, h;
        if (a.Length >= 4 && int.TryParse(a[0], out x) && int.TryParse(a[1], out y) && int.TryParse(a[2], out w) && int.TryParse(a[3], out h))
        {
          bool onScreen = false;
          foreach (Screen sc in Screen.AllScreens)
            if (sc.WorkingArea.IntersectsWith(new Rectangle(x + 40, y + 10, Math.Max(1, w - 80), 30)))
              onScreen = true;
          if (onScreen && w >= 200 && h >= 300) // 画面の外になっていたら既定の位置に出す
            r = new Rectangle(x, y, w, h);
        }
        fs = a.Length >= 5 && a[4] == "full";
      }
      Bounds = r;
      normalBounds = r;
      if (fs)
        SetFullscreen(true);
    }
    void SaveWindow()
    {
      Rectangle r = fullscreen ? normalBounds : (WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);
      Store.Write("window.txt", r.X + " " + r.Y + " " + r.Width + " " + r.Height + (fullscreen ? " full" : ""));
    }

    void SetFullscreen(bool on)
    {
      if (on == fullscreen)
        return;
      fullscreen = on;
      if (on)
      {
        normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        Screen sc = Screen.FromRectangle(normalBounds);
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        Bounds = sc.Bounds;
        Cursor.Hide();
      }
      else
      {
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = normalBounds;
        Cursor.Show();
      }
      Invalidate();
    }

    void Notice(string s)
    {
      osd = s;
      osdT = Game.FPS * 3 / 2;
    }

    // ---- 入力 ----
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
      // 矢印キーやタブをフォームの移動に使わせない
      Keys k = keyData & Keys.KeyCode;
      if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.Tab)
      {
        const int WM_KEYDOWN = 0x100;
        if (msg.Msg == WM_KEYDOWN)
          OnKeyDown(new KeyEventArgs(keyData));
        return true;
      }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
      int k = (int)e.KeyCode;
      bool repeat = k < 256 && keys[k];
      if (k < 256)
        keys[k] = true;
      e.Handled = true;
      e.SuppressKeyPress = e.Alt || e.KeyCode == Keys.F10; // Alt でメニューの音を鳴らさない
      if (repeat)
        return;
      switch (e.KeyCode)
      {
        case Keys.Z:
        case Keys.Space:
        case Keys.J:
          fireHit = true;
          break;
        case Keys.Return:
          if (e.Alt)
            SetFullscreen(!fullscreen);
          else
          {
            startHit = true;
            typed = '\r';
          }
          break;
        case Keys.Escape:
        case Keys.P:
          pauseHit = true;
          break;
        case Keys.Q:
          quitHit = true;
          break;
        case Keys.X:
        case Keys.K: // 遊んでいるあいだは爆弾、メニューでは戻る
          backHit = bombHit = true;
          break;
        case Keys.Back:
          backHit = true;
          typed = '\b';
          break;
        case Keys.Up:
        case Keys.W:
          upHit = true;
          break;
        case Keys.Down:
        case Keys.S:
          downHit = true;
          break;
        case Keys.Left:
        case Keys.A:
          leftHit = true;
          break;
        case Keys.Right:
        case Keys.D:
          rightHit = true;
          break;
        case Keys.F11:
          SetFullscreen(!fullscreen);
          break;
        case Keys.M:
          snd.Muted = !snd.Muted;
          SaveSound();
          Notice(snd.Muted ? "SOUND OFF" : "SOUND ON");
          break;
        case Keys.OemMinus:
        case Keys.Subtract:
          snd.Master = Math.Max(0, snd.Master - 0.1f);
          SaveSound();
          Notice("VOLUME " + (int)Math.Round(snd.Master * 10));
          break;
        case Keys.Oemplus:
        case Keys.Add:
          snd.Master = Math.Min(1, snd.Master + 0.1f);
          SaveSound();
          Notice("VOLUME " + (int)Math.Round(snd.Master * 10));
          break;
      }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
      int k = (int)e.KeyCode;
      if (k < 256)
        keys[k] = false;
      e.Handled = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
      // ネームエントリーで使う文字 (空白は撃つキーなので入れない)。WASD・Z・X などは操作にも使う
      char c = char.ToUpperInvariant(e.KeyChar);
      if (game.mode == Game.Mode.NameEntry && ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '!'))
        typed = c;
      e.Handled = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
      base.OnDeactivate(e);
      Array.Clear(keys, 0, keys.Length);
      if (game.mode == Game.Mode.Play && !game.Paused) // ほかの窓へ移ったら止める
        pauseHit = true;
    }

    bool Key(Keys k) { return keys[(int)k]; }

    Input ReadInput()
    {
      Input inp = new Input();
      inp.Left = Key(Keys.Left) || Key(Keys.A);
      inp.Right = Key(Keys.Right) || Key(Keys.D);
      inp.Up = Key(Keys.Up) || Key(Keys.W);
      inp.Down = Key(Keys.Down) || Key(Keys.S);
      inp.Fire = Key(Keys.Z) || Key(Keys.Space) || Key(Keys.J);
      inp.Slow = Key(Keys.ShiftKey) || Key(Keys.LShiftKey) || Key(Keys.RShiftKey);
      inp.FirePressed = fireHit;
      inp.StartPressed = startHit;
      inp.PausePressed = pauseHit;
      inp.QuitPressed = quitHit;
      inp.BackPressed = backHit;
      inp.BombPressed = bombHit;
      inp.Bomb = Key(Keys.X) || Key(Keys.K);
      inp.UpPressed = upHit;
      inp.DownPressed = downHit;
      inp.LeftPressed = leftHit;
      inp.RightPressed = rightHit;
      inp.Typed = typed;
      fireHit = startHit = pauseHit = quitHit = backHit = bombHit = upHit = downHit = leftHit = rightHit = false;
      typed = '\0';

      Pad.State s;
      if (Pad.Poll(out s))
      {
        int b = s.Buttons, hit = b & ~padPrev;
        padPrev = s.Buttons;
        // 左スティック (遊びを抜いて -1〜1)
        float ax = s.LX / 32767f, ay = -s.LY / 32767f;
        float dead = 0.28f;
        ax = Math.Abs(ax) < dead ? 0 : (ax - Math.Sign(ax) * dead) / (1 - dead);
        ay = Math.Abs(ay) < dead ? 0 : (ay - Math.Sign(ay) * dead) / (1 - dead);
        inp.AX = Math.Max(-1, Math.Min(1, ax));
        inp.AY = Math.Max(-1, Math.Min(1, ay));
        inp.Left |= (b & Pad.LEFT) != 0;
        inp.Right |= (b & Pad.RIGHT) != 0;
        inp.Up |= (b & Pad.UP) != 0 || inp.AY < -0.6f;
        inp.Down |= (b & Pad.DOWN) != 0 || inp.AY > 0.6f;
        inp.Fire |= (b & (Pad.A | Pad.X | Pad.RB)) != 0 || s.RT > 100;
        inp.Slow |= (b & Pad.LB) != 0 || s.LT > 100;
        inp.FirePressed |= (hit & (Pad.A | Pad.X | Pad.RB)) != 0;
        inp.StartPressed |= (hit & Pad.START) != 0;
        inp.PausePressed |= (hit & Pad.START) != 0;
        inp.QuitPressed |= (hit & Pad.BACK) != 0;
        inp.BackPressed |= (hit & Pad.B) != 0;
        inp.BombPressed |= (hit & Pad.B) != 0;
        inp.Bomb |= (b & Pad.B) != 0;
        inp.UpPressed |= (hit & Pad.UP) != 0;
        inp.DownPressed |= (hit & Pad.DOWN) != 0;
        inp.LeftPressed |= (hit & Pad.LEFT) != 0;
        inp.RightPressed |= (hit & Pad.RIGHT) != 0;
      }
      else
        padPrev = 0;

      // XInput でないゲームパッド。割り当ては ESP32 版 (SkyLarkESP32) と同じ:
      // ボタン 1 (SW1) 撃つ、ボタン 2 (SW2) 爆弾・戻る・やめる、ボタン 3 (SW3) 一時停止・スタート、ボタン 4 (押し込み) ゆっくり
      float jx, jy;
      uint jb;
      int pov;
      if (Joy.Poll(out jx, out jy, out jb, out pov))
      {
        uint jhit = jb & ~joyPrev;
        joyPrev = jb;
        if (Math.Abs(jx) > Math.Abs(inp.AX))
          inp.AX = jx;
        if (Math.Abs(jy) > Math.Abs(inp.AY))
          inp.AY = jy;
        // スティックを倒した向きと十字を、矢印キーとして扱う (倒した瞬間も。名前の入力などで使う)
        int dir = pov;
        if (jx <= -0.5f) dir |= Pad.LEFT;
        if (jx >= 0.5f) dir |= Pad.RIGHT;
        if (jy <= -0.5f) dir |= Pad.UP;
        if (jy >= 0.5f) dir |= Pad.DOWN;
        int dhit = dir & ~joyDirPrev;
        joyDirPrev = dir;
        inp.Left |= (dir & Pad.LEFT) != 0;
        inp.Right |= (dir & Pad.RIGHT) != 0;
        inp.Up |= (dir & Pad.UP) != 0;
        inp.Down |= (dir & Pad.DOWN) != 0;
        inp.UpPressed |= (dhit & Pad.UP) != 0;
        inp.DownPressed |= (dhit & Pad.DOWN) != 0;
        inp.LeftPressed |= (dhit & Pad.LEFT) != 0;
        inp.RightPressed |= (dhit & Pad.RIGHT) != 0;
        inp.Fire |= (jb & 0x1) != 0;
        inp.FirePressed |= (jhit & 0x1) != 0;
        inp.Bomb |= (jb & 0x2) != 0;
        inp.BombPressed |= (jhit & 0x2) != 0;
        inp.BackPressed |= (jhit & 0x2) != 0;
        inp.QuitPressed |= (jhit & 0x2) != 0;
        inp.StartPressed |= (jhit & 0x4) != 0;
        inp.PausePressed |= (jhit & 0x4) != 0;
        inp.Slow |= (jb & 0x8) != 0;
      }
      else
      {
        joyPrev = 0;
        joyDirPrev = 0;
      }
      // キーボードと十字は、スティックを倒しきったのと同じ (スティックを倒していないときだけ)
      float kx = (inp.Right ? 1 : 0) - (inp.Left ? 1 : 0), ky = (inp.Down ? 1 : 0) - (inp.Up ? 1 : 0);
      if (inp.AX == 0)
        inp.AX = kx;
      if (inp.AY == 0)
        inp.AY = ky;
      return inp;
    }

    // ---- 進める・描く ----
    public void Tick()
    {
      double now = clock.Elapsed.TotalMilliseconds;
      if (now < nextStep && now < nextFrame)
      {
        Thread.Sleep(1);
        return;
      }
      int n = 0;
      while (now >= nextStep && n < 4) // 描くのが間に合わないときは進めるだけ先にする
      {
        game.Step(ReadInput());
        if (osdT > 0)
          osdT--;
        nextStep += STEP_MS;
        n++;
      }
      if (now >= nextStep) // 大きく遅れたら (窓をつかんでいた等) 追いかけない
        nextStep = now + STEP_MS;
      if (now < nextFrame)
        return;
      nextFrame += FRAME_MS;
      if (nextFrame < now)
        nextFrame = now + FRAME_MS;
      // いまが前の 1 歩から次の 1 歩までのどこか (0〜1) を渡して、そのあいだの位置で描く
      float a = (float)Math.Max(0, Math.Min(1, 1 - (nextStep - now) / STEP_MS));
      game.Draw(screen, a);
      if (osdT > 0)
        Txt.Draw(D.W / 2, D.H / 2 + 40, 0xFFF4D8, osd, true, 3);
      Present();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
      public int biSize, biWidth, biHeight;
      public short biPlanes, biBitCount;
      public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr hdc, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, int[] bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
    [DllImport("gdi32.dll")] static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);
    const uint SRCCOPY = 0x00CC0020, BLACKNESS = 0x00000042;

    void Present()
    {
      Size cs = ClientSize;
      if (cs.Width <= 0 || cs.Height <= 0)
        return;
      // 縦横比を保って、入るいちばん大きな大きさで出す
      float sc = Math.Min((float)cs.Width / D.PW, (float)cs.Height / D.PH);
      int dw = Math.Min(cs.Width, (int)Math.Round(D.PW * sc)), dh = Math.Min(cs.Height, (int)Math.Round(D.PH * sc));
      int dx = (cs.Width - dw) / 2, dy = (cs.Height - dh) / 2;
      BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
      bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
      bi.biWidth = D.PW;
      bi.biHeight = -D.PH; // 上から下へ並ぶ
      bi.biPlanes = 1;
      bi.biBitCount = 32;
      IntPtr hdc = GetDC(Handle);
      // ちょうど 1 倍・2 倍なら点をそのまま、半端な倍率なら色を混ぜてなめらかに
      bool exact = Math.Abs(sc - Math.Round(sc)) < 0.01f && sc >= 0.99f;
      SetStretchBltMode(hdc, exact ? 3 : 4); // 3 = COLORONCOLOR、4 = HALFTONE
      if (!exact)
        SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
      StretchDIBits(hdc, dx, dy, dw, dh, 0, 0, D.PW, D.PH, screen, ref bi, 0, SRCCOPY);
      if (dx > 0)
      {
        PatBlt(hdc, 0, 0, dx, cs.Height, BLACKNESS);
        PatBlt(hdc, dx + dw, 0, cs.Width - dx - dw, cs.Height, BLACKNESS);
      }
      if (dy > 0)
      {
        PatBlt(hdc, 0, 0, cs.Width, dy, BLACKNESS);
        PatBlt(hdc, 0, dy + dh, cs.Width, cs.Height - dy - dh, BLACKNESS);
      }
      ReleaseDC(Handle, hdc);
    }

    protected override void OnPaint(PaintEventArgs e) { Present(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
      SaveWindow();
      snd.Dispose();
      base.OnFormClosing(e);
    }

    // 窓のアイコン: ゲームの戦車を描いて作る
    public static Icon MakeIcon(int size)
    {
      using (Bitmap bm = MakeIconBitmap(size))
        return Icon.FromHandle(bm.GetHicon());
    }
    static Bitmap MakeIconBitmap(int size)
    {
      const int N = Game.ICON;
      int[] px = Game.IconArt();
      Bitmap bm = new Bitmap(size, size, PixelFormat.Format32bppArgb);
      for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
          // 縮めるときは色を平均する
          int r = 0, g = 0, b = 0, a = 0, c = 0;
          int sx0 = x * N / size, sy0 = y * N / size, sx1 = Math.Max(sx0 + 1, (x + 1) * N / size), sy1 = Math.Max(sy0 + 1, (y + 1) * N / size);
          for (int yy = sy0; yy < sy1; yy++)
            for (int xx = sx0; xx < sx1; xx++)
            {
              int v = px[yy * N + xx];
              int al = (v >> 24) & 255;
              a += al;
              r += ((v >> 16) & 255) * al;
              g += ((v >> 8) & 255) * al;
              b += (v & 255) * al;
              c++;
            }
          Color col = a == 0 ? Color.Transparent : Color.FromArgb(a / c, r / a, g / a, b / a);
          bm.SetPixel(x, y, col);
        }
      return bm;
    }

    // exe に埋めるアイコンのファイルを作る (build.bat が最初に 1 回だけ使う)
    public static void WriteIcoFile(string path)
    {
      int[] sizes = { 16, 32, 48, 128 };
      byte[][] pngs = new byte[sizes.Length][];
      for (int i = 0; i < sizes.Length; i++)
        using (Bitmap bm = MakeIconBitmap(sizes[i]))
        using (MemoryStream ms = new MemoryStream())
        {
          bm.Save(ms, ImageFormat.Png);
          pngs[i] = ms.ToArray();
        }
      using (BinaryWriter w = new BinaryWriter(File.Create(path)))
      {
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        int ofs = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
          w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
          w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
          w.Write((byte)0);
          w.Write((byte)0);
          w.Write((short)1);
          w.Write((short)32);
          w.Write(pngs[i].Length);
          w.Write(ofs);
          ofs += pngs[i].Length;
        }
        foreach (byte[] p in pngs)
          w.Write(p);
      }
    }
  }

  static class Program
  {
    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
      public IntPtr hwnd;
      public uint message;
      public IntPtr wParam, lParam;
      public uint time;
      public int x, y;
    }
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
      if (args.Length == 2 && args[0] == "--make-icon")
      {
        MainForm.WriteIcoFile(args[1]);
        return;
      }
      if (args.Length >= 1 && args[0] == "--bench") // 確かめる用: デモを毎フレーム描いて、描くのにかかった時間を出す
      {
        Bench(args);
        return;
      }
      if (args.Length >= 2 && args[0] == "--gallery") // 確かめる用: 敵とボスを並べた絵を書き出す
      {
        Game g = new Game();
        g.Begin();
        for (int k = 0; k < 120; k++)
          g.Step(new Input());
        int[] screen = new int[D.PW * D.PH];
        for (int p = 0; p < 4; p++)
        {
          g.DrawGallery(screen, p, 200);
          SavePng(screen, args[1] + p + ".png");
        }
        return;
      }
      if (args.Length >= 3 && args[0] == "--snap")
      {
        Snap(args);
        return;
      }
      try
      {
        SetProcessDPIAware(); // 拡大表示の設定でぼやけないように、点の数そのままで描く
      }
      catch (Exception)
      {
      }
      Application.EnableVisualStyles();
      timeBeginPeriod(1);
      using (MainForm f = new MainForm())
      {
        Application.Idle += (s, e) =>
        {
          MSG m;
          while (!f.IsDisposed && !PeekMessage(out m, IntPtr.Zero, 0, 0, 0))
            f.Tick();
        };
        Application.Run(f);
      }
      timeEndPeriod(1);
    }

    static void Bench(string[] args)
    {
      Stopwatch sw0 = Stopwatch.StartNew();
      Game g = new Game();
      g.Begin();
      Console.WriteLine("background built in " + sw0.ElapsedMilliseconds + " ms");
      g.autoPilot = true;
      g.StartPlay();
      int[] screen = new int[D.PW * D.PH];
      Stopwatch sw = new Stopwatch();
      double worst = 0;
      int n = 0, frames = args.Length >= 2 ? int.Parse(args[1]) : 3000;
      for (int f = 0; f < frames; f++)
      {
        g.Step(new Input());
        double t0 = sw.Elapsed.TotalMilliseconds;
        sw.Start();
        g.Draw(screen, 0.5f);
        sw.Stop();
        worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
        n++;
      }
      Console.WriteLine(n + " frames, avg " + (sw.Elapsed.TotalMilliseconds / n).ToString("0.00") + " ms, worst " + worst.ToString("0.00") + " ms, zone " + (g.Zone + 1));
    }

    // 確かめる用: 窓を出さずに進めて、指定したフレームの絵を PNG に書き出す。
    // --snap <出力の頭> <フレーム>...
    // 環境変数: FOUNDRY_PLAY=1 で自動で遊ぶ (やられない)、FOUNDRY_SKIP=n で始めに n 区画進める
    static void Snap(string[] args)
    {
      Game g = new Game();
      g.Begin();
      string pv = Environment.GetEnvironmentVariable("FOUNDRY_PLAY");
      bool play = pv == "1" || pv == "2"; // 2 は自動で動かさない (やられるまで)
      string skip = Environment.GetEnvironmentVariable("FOUNDRY_SKIP");
      if (play)
      {
        g.autoPilot = pv == "1";
        g.StartPlay();
      }
      if (skip != null && skip.Length > 0)
        for (int i = 0; i < int.Parse(skip); i++)
          g.SkipZone();
      int[] screen = new int[D.PW * D.PH];
      int last = 0;
      for (int i = 2; i < args.Length; i++)
        last = Math.Max(last, int.Parse(args[i]));
      for (int f = 1; f <= last; f++)
      {
        Input inp = new Input();
        g.Step(inp);
        for (int i = 2; i < args.Length; i++)
          if (int.Parse(args[i]) == f)
          {
            g.Draw(screen);
            SavePng(screen, args[1] + f + ".png");
            Console.WriteLine(f + " " + g.mode + " score " + g.Score + " zone " + (g.Zone + 1));
          }
      }
    }

    static void SavePng(int[] screen, string path)
    {
      using (Bitmap bm = new Bitmap(D.PW, D.PH, PixelFormat.Format32bppRgb))
      {
        BitmapData bd = bm.LockBits(new Rectangle(0, 0, bm.Width, bm.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        Marshal.Copy(screen, 0, bd.Scan0, screen.Length);
        bm.UnlockBits(bd);
        bm.Save(path, ImageFormat.Png);
      }
    }
  }
}
