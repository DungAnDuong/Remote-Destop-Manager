using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RDM.Controller.Theming;

namespace RDM.Controller.Controls;

/// <summary>
/// Bảng dữ liệu kiểu VirtualMode: chỉ vẽ dòng đang nhìn thấy, nên chịu được hàng nghìn thiết bị.
/// Bấm tiêu đề cột để sắp xếp trong bộ nhớ.
/// </summary>
public class GridView<T> : DataGridView where T : class
{
    private readonly List<Func<T, int, object>> _getters = new List<Func<T, int, object>>();
    private List<T> _items = new List<T>();
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    public GridView()
    {
        DoubleBuffered = true;
        VirtualMode = true;
        ReadOnly = true;
        AllowUserToAddRows = AllowUserToDeleteRows = AllowUserToResizeRows = false;
        RowHeadersVisible = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        MultiSelect = false;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        BorderStyle = BorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        EnableHeadersVisualStyles = false;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        ColumnHeadersHeight = Dpi.S(36);
        RowTemplate.Height = Dpi.S(32);
        Dock = DockStyle.Fill;
        ShowCellToolTips = true;
        StandardTab = true;
        ApplyTheme();
        CellValueNeeded += OnCellValueNeeded;
        ColumnHeaderMouseClick += OnHeaderClick;
    }

    public GridView<T> AddColumn(string header, Func<T, object> getter, int weight = 100) => AddColumn(header, (item, _) => getter(item), weight);

    public GridView<T> AddColumn(string header, Func<T, int, object> getter, int weight = 100)
    {
        _getters.Add(getter);
        Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Programmatic, MinimumWidth = Dpi.S(30),
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(Dpi.S(6), 0, Dpi.S(4), 0) },
            HeaderCell = { Style = { Padding = new Padding(Dpi.S(6), 0, Dpi.S(4), 0) } }
        });
        return this;
    }

    public void ApplyTheme()
    {
        BackgroundColor = Theme.Surface;
        GridColor = Theme.Border;
        DefaultCellStyle.BackColor = Theme.Surface;
        DefaultCellStyle.ForeColor = Theme.Text;
        DefaultCellStyle.SelectionBackColor = Theme.Selection;
        DefaultCellStyle.SelectionForeColor = Theme.SelectionText;
        DefaultCellStyle.Font = Theme.Font(9.5f);
        ColumnHeadersDefaultCellStyle.BackColor = Theme.SurfaceAlt;
        ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextSecondary;
        ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.SurfaceAlt;
        ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.TextSecondary;
        ColumnHeadersDefaultCellStyle.Font = Theme.Font(9f, true);
    }

    public IReadOnlyList<T> Items => _items;
    public T SelectedItem => CurrentRow != null && CurrentRow.Index >= 0 && CurrentRow.Index < _items.Count ? _items[CurrentRow.Index] : null;

    /// <summary>Nạp dữ liệu, giữ nguyên thứ tự sắp xếp và cố gắng giữ dòng đang chọn.</summary>
    public void SetItems(IEnumerable<T> items, Func<T, T, bool> sameItem = null)
    {
        var selected = SelectedItem;
        var scroll = FirstDisplayedScrollingRowIndex;
        RowCount = 0;
        _items = items.ToList();
        ApplySort();
        RowCount = _items.Count;
        if (selected != null && sameItem != null)
        {
            var index = _items.FindIndex(x => sameItem(x, selected));
            if (index >= 0 && Columns.Count > 0) CurrentCell = Rows[index].Cells[0];
        }
        if (scroll >= 0 && scroll < RowCount) FirstDisplayedScrollingRowIndex = scroll;
        Invalidate();
    }

    private void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex >= 0 && e.RowIndex < _items.Count && e.ColumnIndex < _getters.Count)
            e.Value = _getters[e.ColumnIndex](_items[e.RowIndex], e.RowIndex);
    }

    private void OnHeaderClick(object sender, DataGridViewCellMouseEventArgs e)
    {
        if (_sortColumn == e.ColumnIndex) _sortAscending = !_sortAscending;
        else { _sortColumn = e.ColumnIndex; _sortAscending = true; }
        foreach (DataGridViewColumn c in Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        Columns[_sortColumn].HeaderCell.SortGlyphDirection = _sortAscending ? SortOrder.Ascending : SortOrder.Descending;
        ApplySort();
        Invalidate();
    }

    private void ApplySort()
    {
        if (_sortColumn < 0 || _sortColumn >= _getters.Count) return;
        var getter = _getters[_sortColumn];
        var keyed = _items.Select((item, i) => (item, key: getter(item, i)));
        var comparer = Comparer<object>.Create((a, b) =>
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;
            return a.GetType() == b.GetType() && a is IComparable c ? c.CompareTo(b) : string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase);
        });
        _items = (_sortAscending ? keyed.OrderBy(x => x.key, comparer) : keyed.OrderByDescending(x => x.key, comparer)).Select(x => x.item).ToList();
    }
}
