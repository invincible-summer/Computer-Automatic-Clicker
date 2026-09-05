namespace MacroClicker;

/// <summary>通用输入对话框：标题 + 提示 + 单行文本；「确定」返回 Trim 后文本，「取消」返回 null。</summary>
internal static class InputDialog
{
    public static string? Show(IWin32Window owner, string title, string label, string value)
    {
        string? result = null;
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            ClientSize = new Size(400, 158),
            Font = UiTheme.BaseFont
        };
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(16, 18) };
        var box = new TextBox { Text = value, Width = 352 };
        var wrap = UiTheme.Wrap(box);
        wrap.Location = new Point(14, 46);
        var ok = new AppButton { Text = "确定", Variant = AppVariant.Primary, AutoSize = true, Padding = new Padding(16, 5, 16, 5) };
        var cancel = new AppButton { Text = "取消", Variant = AppVariant.Ghost, AutoSize = true, Padding = new Padding(16, 5, 16, 5) };
        ok.Location = new Point(form.ClientSize.Width - 14 - ok.Width, form.ClientSize.Height - 12 - ok.Height);
        cancel.Location = new Point(ok.Left - 8 - cancel.Width, ok.Top);
        box.TextChanged += (s, e) => ok.Enabled = box.Text.Trim().Length > 0;
        ok.Click += (s, e) =>
        {
            result = box.Text.Trim();
            form.DialogResult = DialogResult.OK;
        };
        cancel.Click += (s, e) => form.DialogResult = DialogResult.Cancel;
        form.Controls.AddRange(new Control[] { lbl, wrap, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        UiTheme.Apply(form);
        form.Shown += (s, e) => { box.Focus(); box.SelectAll(); };
        form.ShowDialog(owner);
        return result;
    }
}

/// <summary>
/// 宏库选择对话框：模糊搜索（前缀 &gt; 包含 &gt; 按序子序列）+ 打开 / 新建 / 重命名 / 删除。
/// 双击或回车打开；对话框内发生的重命名/删除会记录在 Renamed / Deleted 中，供主窗体同步当前宏。
/// </summary>
internal sealed class MacroPickerForm : Form
{
    public enum PickAction { None, Open, Create }

    private sealed record MacroRow(string Name, string Path, int Count, DateTime Modified);

    public PickAction Action { get; private set; } = PickAction.None;
    public string? MacroName { get; private set; }
    public IReadOnlyDictionary<string, string> Renamed => _renamed;
    public IReadOnlyCollection<string> Deleted => _deleted;

    private readonly MacroTarget _target;
    private readonly Dictionary<string, string> _renamed = new();
    private readonly HashSet<string> _deleted = new();
    private readonly List<MacroRow> _all = new();
    private readonly TextBox _search;
    private readonly AppListView _lv;
    private readonly AppButton _btnOpen, _btnNew, _btnRename, _btnDelete, _btnCancel;
    private readonly Label _lblHint;

    public MacroPickerForm(MacroTarget target, string? currentName)
    {
        _target = target;
        Text = target == MacroTarget.Emulator ? "宏库 · 模拟器" : "宏库 · Windows 本机";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 452);
        Font = UiTheme.BaseFont;

        var top = new Panel { Dock = DockStyle.Top, Height = 50 };
        var lblSearch = new Label { Text = "搜索:", AutoSize = true, Location = new Point(14, 17) };
        _search = new TextBox
        {
            Width = 430,
            PlaceholderText = "名称模糊搜索 · 回车打开第一个"
        };
        var wrap = UiTheme.Wrap(_search);
        wrap.Location = new Point(64, 12);
        wrap.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(lblSearch);
        top.Controls.Add(wrap);

        _lv = new AppListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false
        };
        _lv.Columns.Add("名称", 250);
        _lv.Columns.Add("事件数", 64);
        _lv.Columns.Add("修改时间", 120);
        UiTheme.StyleList(_lv);
        _lv.DoubleClick += (s, e) => OpenSelected();
        _lv.SelectedIndexChanged += (s, e) => RefreshButtons();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58 };
        _btnOpen = Mk("打开", AppVariant.Primary);
        _btnNew = Mk("新建", AppVariant.Neutral);
        _btnRename = Mk("重命名", AppVariant.Ghost);
        _btnDelete = Mk("删除", AppVariant.Danger);
        _btnCancel = Mk("取消", AppVariant.Ghost);
        _lblHint = new Label
        {
            Text = "",
            AutoSize = false,
            Height = 20,
            TextAlign = ContentAlignment.MiddleLeft,
            Tag = "sub"
        };
        bottom.Controls.AddRange(new Control[] { _btnOpen, _btnNew, _btnRename, _btnDelete, _btnCancel, _lblHint });
        bottom.Resize += (s, e) => LayoutBottom(bottom);
        _btnOpen.Click += (s, e) => OpenSelected();
        _btnNew.Click += (s, e) => CreateNew();
        _btnRename.Click += (s, e) => RenameSelected();
        _btnDelete.Click += (s, e) => DeleteSelected();
        _btnCancel.Click += (s, e) => Close();

        _search.TextChanged += (s, e) => RebuildItems();
        _search.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (_lv.Items.Count > 0)
                {
                    _lv.Items[0].Selected = true;
                    OpenSelected();
                }
            }
        };

        Controls.Add(_lv);
        Controls.Add(top);
        Controls.Add(bottom);
        AcceptButton = _btnOpen;
        CancelButton = _btnCancel;

        UiTheme.Apply(this);
        LoadRows();
        RebuildItems();
        if (currentName != null)
        {
            foreach (ListViewItem it in _lv.Items)
                if (it.Text == currentName)
                {
                    it.Selected = true;
                    it.EnsureVisible();
                    break;
                }
        }
        ActiveControl = _search;
    }

    private static AppButton Mk(string text, AppVariant v) => new()
    {
        Text = text,
        Variant = v,
        AutoSize = true,
        Padding = new Padding(14, 5, 14, 5)
    };

    private void LayoutBottom(Panel bottom)
    {
        // 右对齐：取消 · 删除 · 重命名 · 新建 · 打开
        var order = new[] { _btnCancel, _btnDelete, _btnRename, _btnNew, _btnOpen };
        int x = bottom.ClientSize.Width - 14;
        foreach (var b in order)
        {
            x -= b.Width;
            b.Location = new Point(x, Math.Max(0, (bottom.ClientSize.Height - b.Height) / 2));
            x -= 7;
        }
        _lblHint.Location = new Point(14, (bottom.ClientSize.Height - _lblHint.Height) / 2);
        _lblHint.Width = Math.Max(0, _btnOpen.Left - 24);
    }

    private void LoadRows()
    {
        _all.Clear();
        foreach (var (name, path) in MacroStore.ListMacros(_target))
        {
            int count = -1;
            try { count = MacroStore.Load(path).Item2.Count; } catch { }
            _all.Add(new MacroRow(name, path, count, File.GetLastWriteTime(path)));
        }
    }

    /// <summary>模糊匹配得分：0=前缀命中，1=包含，2=按序子序列，-1=不匹配。</summary>
    private static int MatchScore(string name, string q)
    {
        if (q.Length == 0) return 1;
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 1;
        int i = 0;
        foreach (var ch in name)
        {
            if (char.ToLowerInvariant(ch) == char.ToLowerInvariant(q[i]))
            {
                i++;
                if (i == q.Length) return 2;
            }
        }
        return -1;
    }

    private void RebuildItems()
    {
        var q = _search.Text.Trim();
        var rows = _all.Select(r => (Row: r, Score: MatchScore(r.Name, q)))
            .Where(t => t.Score >= 0)
            .OrderBy(t => t.Score)
            .ThenByDescending(t => t.Row.Modified)
            .Select(t => t.Row)
            .ToList();
        _lv.BeginUpdate();
        _lv.Items.Clear();
        foreach (var r in rows)
        {
            var it = new ListViewItem(r.Name) { Tag = r };
            it.SubItems.Add(r.Count < 0 ? "—" : r.Count.ToString());
            it.SubItems.Add(r.Modified.ToString("MM-dd HH:mm"));
            _lv.Items.Add(it);
        }
        _lv.EndUpdate();
        _lblHint.Text = rows.Count == 0 && q.Length > 0
            ? $"无匹配（共 {_all.Count} 个宏）"
            : q.Length == 0 ? $"共 {_all.Count} 个宏" : $"匹配 {rows.Count} / {_all.Count} 个宏";
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool one = _lv.SelectedItems.Count == 1;
        _btnOpen.Enabled = one;
        _btnRename.Enabled = one;
        _btnDelete.Enabled = one;
    }

    private void OpenSelected()
    {
        if (_lv.SelectedItems.Count != 1) return;
        Action = PickAction.Open;
        MacroName = _lv.SelectedItems[0].Text;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CreateNew()
    {
        string? name = null;
        while (name == null)
        {
            var input = InputDialog.Show(this, "新建宏", "宏名称：", DefaultName());
            if (input == null) return;
            var candidate = MacroStore.SanitizeName(input);
            if (!File.Exists(MacroStore.PathOf(_target, candidate)))
            {
                name = candidate;
                break;
            }
            MessageBox.Show(this, $"宏「{candidate}」已存在，请换一个名称。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        Action = PickAction.Create;
        MacroName = name;
        DialogResult = DialogResult.OK;
        Close();
    }

    private string DefaultName()
    {
        var names = _all.Select(r => r.Name).ToHashSet();
        for (int i = 1; ; i++)
        {
            var n = $"宏 {i}";
            if (!names.Contains(n)) return n;
        }
    }

    private void RenameSelected()
    {
        if (_lv.SelectedItems.Count != 1) return;
        var old = _lv.SelectedItems[0].Text;
        var input = InputDialog.Show(this, "重命名宏", "新名称：", old);
        if (input == null) return;
        var name = MacroStore.SanitizeName(input);
        if (name == old) return;
        try
        {
            MacroStore.Rename(_target, old, name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重命名失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _renamed[old] = name;
        _deleted.Remove(old);
        LoadRows();
        RebuildItems();
        foreach (ListViewItem it in _lv.Items)
            if (it.Text == name)
            {
                it.Selected = true;
                it.EnsureVisible();
                break;
            }
    }

    private void DeleteSelected()
    {
        if (_lv.SelectedItems.Count != 1) return;
        var name = _lv.SelectedItems[0].Text;
        if (MessageBox.Show(this, $"确定删除宏「{name}」？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        MacroStore.DeleteMacro(MacroStore.PathOf(_target, name));
        _deleted.Add(name);
        _renamed.Remove(name);
        LoadRows();
        RebuildItems();
    }
}
