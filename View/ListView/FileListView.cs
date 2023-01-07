﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GeoTagNinja.Model;
using NLog;
using static System.Environment;

namespace GeoTagNinja.View.ListView;
/*
 * TODOs
 * * Check if to migrate Context Menu into this class
 * * Hide Items.Clear
 * * When EXIFTool, etc do not use the value of column "Text" anymore,
 *   remove adding item extension and showing file system dir names
 */

public partial class FileListView : System.Windows.Forms.ListView
{

    /// <summary>
    /// Class containing native method (shell32, etc) definitions in order
    /// to retrieve file and directory information.
    /// 
    /// This is to deal with the icons in listview
    /// from https://stackoverflow.com/a/37806517/3968494
    /// </summary>
    [SuppressMessage(category: "ReSharper", checkId: "InconsistentNaming"), SuppressMessage(category: "ReSharper", checkId: "UnusedMember.Local"), SuppressMessage(category: "ReSharper", checkId: "IdentifierTypo"), SuppressMessage(category: "ReSharper", checkId: "StringLiteralTypo"), SuppressMessage(category: "ReSharper", checkId: "MemberCanBePrivate.Local"), SuppressMessage(category: "ReSharper", checkId: "FieldCanBeMadeReadOnly.Local")]
    private static class NativeMethods
    {
        public const uint LVM_FIRST = 0x1000;
        public const uint LVM_GETIMAGELIST = LVM_FIRST + 2;
        public const uint LVM_SETIMAGELIST = LVM_FIRST + 3;

        public const uint LVSIL_NORMAL = 0;
        public const uint LVSIL_SMALL = 1;
        public const uint LVSIL_STATE = 2;
        public const uint LVSIL_GROUPHEADER = 3;

        public const uint SHGFI_DISPLAYNAME = 0x200;
        public const uint SHGFI_ICON = 0x100;
        public const uint SHGFI_LARGEICON = 0x0;
        public const uint SHGFI_SMALLICON = 0x1;
        public const uint SHGFI_SYSICONINDEX = 0x4000;

        [DllImport(dllName: "user32")]
        public static extern IntPtr SendMessage(IntPtr hWnd,
                                                uint msg,
                                                uint wParam,
                                                IntPtr lParam);

        [DllImport(dllName: "comctl32")]
        public static extern bool ImageList_Destroy(IntPtr hImageList);

        [DllImport(dllName: "shell32", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath,
                                                  uint dwFileAttributes,
                                                  ref SHFILEINFOW psfi,
                                                  uint cbSizeFileInfo,
                                                  uint uFlags);

        [DllImport(dllName: "uxtheme", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd,
                                                string pszSubAppName,
                                                string pszSubIdList);


        [StructLayout(layoutKind: LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFOW
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(unmanagedType: UnmanagedType.ByValTStr, SizeConst = 260 * 2)]
            public string szDisplayName;

            [MarshalAs(unmanagedType: UnmanagedType.ByValTStr, SizeConst = 80 * 2)]
            public string szTypeName;
        }


        [StructLayout(layoutKind: LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Shfileinfow
        {
            public readonly IntPtr hIcon;
            public readonly int iIcon;
            public readonly uint dwAttributes;

            [MarshalAs(unmanagedType: UnmanagedType.ByValTStr, SizeConst = 260 * 2)]
            public readonly string szDisplayName;

            [MarshalAs(unmanagedType: UnmanagedType.ByValTStr, SizeConst = 80 * 2)]
            public readonly string szTypeName;
        }
    }


    /// <summary>
    /// Class containing all the relevant column names to be used
    /// when e.g. querying for information.
    /// </summary>
    public static class ColumnNames
    {
        public static string FILENAME = "FileName";
        public static string COORDINATES = "Coordinates";
    }


    // Default values to set for entries
    public static string UNKNOWN_VALUE_FILE = "-";
    // Note - if this is changed, all checks for unknown need to be udpated
    // because currently this works via item.replace and check versus ""
    // but replace did not take ""
    public static string UNKNOWN_VALUE_DIR = "";

    /// <summary>
    /// Every column has this prefix for its name when it is created.
    /// </summary>
    public static string COL_NAME_PREFIX = "clh_";

    #region internal vars

    internal static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     The used application language
    /// </summary>
    private string _AppLanguage = "";

    /// <summary>
    ///     The list of columns to show (without prefix)
    /// </summary>
    internal List<string> _cfg_Col_Names = new List<string>();

    /// <summary>
    ///     The default order of the columns to show (without prefix)
    /// </summary>
    internal Dictionary<string, int> _cfg_Col_Order_Default = new Dictionary<string, int>();

    /// <summary>
    ///     The used sorter
    /// </summary>
    internal ListViewColumnSorter LvwColumnSorter;

    /// <summary>
    ///     Tracks if the initializer ReadAndApplySetting was called.
    /// </summary>
    internal bool _isInitialized = false;

    /// <summary>
    /// Counter for files in the list - incremented in addListItem method.
    /// </summary>
    internal int _fileCount = -1;

    /// <summary>
    /// Pointer to the SHFILEINFO Structure that is initialized to be
    /// used for this list view.
    /// </summary>
    private NativeMethods.SHFILEINFOW shfi = new();

    #endregion


    #region External Visible Properties

    /// <summary>
    ///     The list of directory elements to display.
    /// </summary>
    public DirectoryElementCollection DirectoryElements { get; private set; } = new();

    /// <summary>
    /// The number of elements of type file in the view as
    /// loaded.
    /// </summary>
    public int FileCount { get { return _fileCount; } }

    #endregion


    /// <summary>
    ///     Constructor
    /// </summary>
    public FileListView()
    {
        Logger.Info(message: "Creating List View ...");
        InitializeComponent();
    }


    #region Internal Update Logic

    /// <summary>
    ///     Adds a new listitem to lvw_FileList listview
    /// </summary>
    /// <param name="fileNameWithoutPath">Name of file to be added</param>
    private void addListItem(DirectoryElement item)
    {
        #region icon handlers

        //https://stackoverflow.com/a/37806517/3968494
        // Get the items from the file system, and add each of them to the ListView,
        // complete with their corresponding name and icon indices.
        
        IntPtr himl;

        if (item.Type != DirectoryElement.ElementType.MyComputer)
        {
            himl = NativeMethods.SHGetFileInfo(pszPath: item.FullPathAndName,
                                               dwFileAttributes: 0,
                                               psfi: ref shfi,
                                               cbSizeFileInfo: (uint)Marshal.SizeOf(structure: shfi),
                                               uFlags: NativeMethods.SHGFI_DISPLAYNAME | NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_SMALLICON);
        }
        else
        {
            himl = NativeMethods.SHGetFileInfo(pszPath: item.ItemName,
                                               dwFileAttributes: 0,
                                               psfi: ref shfi,
                                               cbSizeFileInfo: (uint)Marshal.SizeOf(structure: shfi),
                                               uFlags: NativeMethods.SHGFI_DISPLAYNAME | NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_SMALLICON);
        }

        //Debug.Assert(himl == hSysImgList); // should be the same imagelist as the one we set

        #endregion

        // Create a default ("empty") set of col entries for the new list item entry
        List<string> subItemList = new();
        if (item.Type == DirectoryElement.ElementType.File)
        {
            foreach (ColumnHeader columnHeader in Columns)
            {
                if (columnHeader.Name != COL_NAME_PREFIX + ColumnNames.FILENAME)
                {
                    subItemList.Add(item: UNKNOWN_VALUE_FILE);
                }
            }
            // For each non-file (i.e. dirs), create empty sub items (needed for sorting)
        }
        else
        {
            foreach (ColumnHeader columnHeader in Columns)
            {
                if (columnHeader.Name != COL_NAME_PREFIX + ColumnNames.FILENAME)
                {
                    subItemList.Add(item: UNKNOWN_VALUE_DIR);
                }
            }
        }

        ListViewItem lvi = new();

        item.DisplayName = shfi.szDisplayName;

        #region Set LVI Text depending on whether displayname is usable for FS operations

        // dev comment --> https://docs.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shgetfileinfow
        // SHGFI_DISPLAYNAME (0x000000200)
        // Retrieve the display name for the file, which is the name as it appears in Windows Explorer.
        // The name is copied to the szDisplayName member of the structure specified in psfi.
        // The returned display name uses the long file name, if there is one, rather than the 8.3 form of the file name.
        // [!!!!] Note that the display name can be affected by settings such as whether extensions are shown.

        // TLDR if Windows User has "show extensions" set to OFF in Windows Explorer, they won't show here either.
        // The repercussions of that is w/o an extension fileinfo.exists will return false and exiftool won't run/find it.

        // With that in mind if we're missing the extension then we'll force it back on.
        if (!string.IsNullOrEmpty(value: item.Extension))
        {
            if (shfi.szDisplayName.Contains(value: item.Extension))
            {
                lvi.Text = shfi.szDisplayName;
            }
            else
            {
                lvi.Text = shfi.szDisplayName + item.Extension;
            }
        }
        else
        {
            // this should prevent showing silly string values for special folder
            // (like if your Pictures folder has been moved to say Digi, it'd have
            // shown "Digi" but since that doesn't exist per se it'd have caused
            // an error.
            // same for non-English places. E.g. "Documents and Settings" in HU
            // would be displayed as "Felhasználók" but that folder is still
            // actually called Documents and Settings, but the label is "fake".
            if (item.Type == DirectoryElement.ElementType.SubDirectory ||
                item.Type == DirectoryElement.ElementType.MyComputer ||
                item.Type == DirectoryElement.ElementType.ParentDirectory)
            {
                lvi.Text = item.ItemName;
            }
            else
            {
                lvi.Text = shfi.szDisplayName;
            }
        }

        #endregion

        // Set the icon to use out of the explorer icons
        lvi.ImageIndex = shfi.iIcon;

        // File items (to be parsed by exif tool) set in gray
        if (item.Type == DirectoryElement.ElementType.File)
        {
            lvi.ForeColor = Color.Gray;
        }

        // Show progress every 10th item
        if (lvi.Index % 10 == 0)
        {
            Application.DoEvents();
            // not adding the xmp here because the current code logic would pull a "unified" data point.                         

            ScrollToDataPoint(itemText: item.ItemName);
        }

        // don't add twice. this could happen if user does F5 too fast/too many times/is derp. (mostly the last one.)
        if (FindItemWithText(text: lvi.Text) == null)
        {
            lvi.Tag = item;
            Items.Add(value: lvi)
                .SubItems.AddRange(items: subItemList.ToArray());
        }
    }

    #endregion


    #region Column Size and Order

    /// <summary>
    ///     Reads the widths of individual CLHs from SQL
    /// </summary>
    /// <exception cref="InvalidOperationException">If it encounters a missing CLH</exception>
    private void ColOrderAndWidth_Read()
    {
        Logger.Debug(message: "Starting");

        BeginUpdate(); // stop drawing

        // While reading col widths, gather order data
        List<int> colOrderIndex = new();
        List<string> colOrderHeadername = new();

        string settingIdToSend;
        string colWidth = null;
        // logic: see if it's in SQL first...if not then set to Auto
        foreach (ColumnHeader columnHeader in Columns)
        {
            // columnHeader.Name doesn't get automatically recorded, i think that's a VSC bug.
            // anyway will introduce a breaking-line here for that.
            // oh and can't convert bool to str but it's not letting to deal w it otherwise anyway so going for length == 0 instead
            if (columnHeader.Name.Length == 0)
            {
                throw new InvalidOperationException(message: "columnHeader name missing");
            }

            // Read index / order
            settingIdToSend = Name + "_" + columnHeader.Name + "_index";
            colOrderHeadername.Add(item: columnHeader.Name);
            int colOrderIndexInt = 0;

            colOrderIndexInt = Convert.ToInt16(value: HelperStatic.DataReadSQLiteSettings(
                                                   tableName: "applayout",
                                                   settingTabPage: "lvw_FileList",
                                                   settingId: settingIdToSend));

            // If no user preset is found, retrieve the default
            // col order value
            if (colOrderIndexInt == 0)
            {
                if (_cfg_Col_Order_Default.ContainsKey(key: columnHeader.Name.Substring(startIndex: 4)))
                {
                    colOrderIndexInt = _cfg_Col_Order_Default[columnHeader.Name.Substring(startIndex: 4)];
                }
            }

            colOrderIndex.Add(item: colOrderIndexInt);

            Logger.Trace(message: "columnHeader: " +
                                  columnHeader.Name +
                                  " - colOrderIndex: " +
                                  colOrderIndexInt);

            // Read and process width
            settingIdToSend = Name + "_" + columnHeader.Name + "_width";
            colWidth = HelperStatic.DataReadSQLiteSettings(
                tableName: "applayout",
                settingTabPage: "lvw_FileList",
                settingId: settingIdToSend
            );

            // We only set col width if there actually is a setting for it.
            // New columns thus will have a default size
            if (colWidth != null && colWidth.Length > 0)
            {
                columnHeader.Width = Convert.ToInt16(value: colWidth);
            }

            Logger.Trace(message: "columnHeader: " +
                                  columnHeader.Name +
                                  " - columnHeader.Width: " +
                                  columnHeader.Width);
        }

        // Finally set the column order - setting them from first to last col
        int[] arrColOrderIndex = colOrderIndex.ToArray();
        string[] arrColOrderHeadername = colOrderHeadername.ToArray();
        Array.Sort(keys: arrColOrderIndex, items: arrColOrderHeadername);
        for (int idx = 0; idx < arrColOrderHeadername.Length; idx++)
        {
            foreach (ColumnHeader columnHeader in Columns)
            {
                // We go for case-insensitive!
                if (string.Equals(a: columnHeader.Name, b: arrColOrderHeadername[idx], comparisonType: StringComparison.OrdinalIgnoreCase))
                {
                    columnHeader.DisplayIndex = idx;
                    Logger.Trace(message: "columnHeader: " +
                                          columnHeader.Name +
                                          " - columnHeader.DisplayIndex: " +
                                          columnHeader.DisplayIndex);
                    break;
                }
            }
        }

        EndUpdate(); // continue drawing
    }


    /// <summary>
    ///     Sends the CLH width and column order to SQL for writing.
    /// </summary>
    private void ColOrderAndWidth_Write()
    {
        string settingIdToSend;
        foreach (ColumnHeader columnHeader in Columns)
        {
            settingIdToSend = Name + "_" + columnHeader.Name + "_index";
            HelperStatic.DataWriteSQLiteSettings(
                tableName: "applayout",
                settingTabPage: "lvw_FileList",
                settingId: settingIdToSend,
                settingValue: columnHeader.DisplayIndex.ToString()
            );

            settingIdToSend = Name + "_" + columnHeader.Name + "_width";
            HelperStatic.DataWriteSQLiteSettings(
                tableName: "applayout",
                settingTabPage: "lvw_FileList",
                settingId: settingIdToSend,
                settingValue: columnHeader.Width.ToString()
            );
        }
    }


    /// <summary>
    ///     Shows the dialog to selection which columns to show.
    /// </summary>
    public void ShowColumnSelectionDialog()
    {
        FrmColumnSelection frm_ColSel = new(
            ColList: Columns, AppLanguage: _AppLanguage);
        Point lvwLoc = PointToScreen(p: new Point(x: 0, y: 0));
        lvwLoc.Offset(dx: 20, dy: 10); // Relative to list view top left
        frm_ColSel.Location = lvwLoc; // in screen coords...
        frm_ColSel.ShowDialog(owner: this);
    }

    #endregion


    #region Further Settings Stuff


    /// <summary>
    ///     Setup the columns as read from the data table.
    /// </summary>
    private void SetupColumns()
    {
        Logger.Debug(message: "Starting");

        foreach (string colName in _cfg_Col_Names)
        {
            ColumnHeader clh = new();
            clh.Name = COL_NAME_PREFIX + colName;
            Columns.Add(value: clh);
            Logger.Trace(message: "Added column: " + colName);
        }

        // Encapsulate locatization - in case it fails above column setup still there...
        try
        {
            foreach (ColumnHeader clh in Columns)
            {
                Logger.Trace(message: "Loading localization for: " + clh.Name);
                clh.Text = HelperStatic.DataReadDTObjectText(
                    objectType: "ColumnHeader",
                    objectName: clh.Name
                );
                Logger.Trace(message: "Loaded localization: " + clh.Name + " --> " + clh.Text);
            }
        }
        catch (Exception ex)
        {
            Logger.Fatal(message: "Error: " + ex.Message);
            MessageBox.Show(text: HelperStatic.GenericGetMessageBoxText(messageBoxName: "mbx_FrmMainApp_ErrorLanguageFileColumnHeaders") + ex.Message, caption: "Error", buttons: MessageBoxButtons.OK, icon: MessageBoxIcon.Error);
        }
    }


    /// <summary>
    /// Extract the column names as well as their default ordering
    /// from the given data table.
    /// </summary>
    /// <param name="dt"></param>
    private void ExtractConfigInfoFromDT(DataTable dt)
    {
        // The columns to look in
        string CFGCOL_COL_NAME = "objectName";
        string CFGCOL_COL_ORDER_DEFAULT = "sqlOrder";

        int backup_order_idx = 999;

        dt.DefaultView.Sort = "sqlOrder";
        foreach (DataRow row in dt.DefaultView.ToTable().Rows)
        {
            string colName = row[CFGCOL_COL_NAME].ToString();
            _cfg_Col_Names.Add(colName);
            try
            {
                _cfg_Col_Order_Default[colName] = int.Parse(s: row[CFGCOL_COL_ORDER_DEFAULT].ToString());
            }
            catch
            {
                // There was no default set (or it was invalid) -> set backup default
                Logger.Warn(message: "No order default found for column: " + colName);
                _cfg_Col_Order_Default[CFGCOL_COL_NAME] = backup_order_idx;
                backup_order_idx += 1;
            }
        }
    }


    private void SetStyle()
    {
        // Set up the ListView control's basic properties.
        // Set its theme so it will look like the one used by Explorer.
        NativeMethods.SetWindowTheme(hWnd: Handle, pszSubAppName: "Explorer", pszSubIdList: null);
    }

    private void InitializeImageList()
    {
        //https://stackoverflow.com/a/37806517/3968494
        shfi = new();
        IntPtr hSysImgList = NativeMethods.SHGetFileInfo(pszPath: "",
                                                         dwFileAttributes: 0,
                                                         psfi: ref shfi,
                                                         cbSizeFileInfo: (uint)Marshal.SizeOf(structure: shfi),
                                                         uFlags: NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_SMALLICON);
        Debug.Assert(condition: hSysImgList != IntPtr.Zero); // cross our fingers and hope to succeed!

        // Set the ListView control to use that image list.
        IntPtr hOldImgList = NativeMethods.SendMessage(hWnd: Handle,
                                                       msg: NativeMethods.LVM_SETIMAGELIST,
                                                       wParam: NativeMethods.LVSIL_SMALL,
                                                       lParam: hSysImgList);

        // If the ListView control already had an image list, delete the old one.
        if (hOldImgList != IntPtr.Zero)
        {
            NativeMethods.ImageList_Destroy(hImageList: hOldImgList);
        }
    }


    /// <summary>
    ///     Initialize the list view.
    ///     
    /// Must be called before items are added to it.
    /// </summary>
    /// <param name="appLanguage">The application language to use</param>
    /// <param name="objectNames">A data table containing the list of
    /// columns to be used in column "objectName" and the default ordering
    /// of these in column "sqlOrder"</param>
    /// <exception cref="InvalidOperationException">If this method is called
    /// more than once.</exception>
    public void ReadAndApplySetting(string appLanguage,
                                    DataTable objectNames)
    {
        if (_isInitialized) throw new InvalidOperationException("Trying to initialize the FileListView more than once.");

        Logger.Debug(message: "Starting");
        _AppLanguage = appLanguage;

        ExtractConfigInfoFromDT(objectNames);
        SetupColumns();

        // Create the sorter for the list view
        LvwColumnSorter = new ListViewColumnSorter();
        ListViewItemSorter = LvwColumnSorter;

        // Apply column order and size
        ColOrderAndWidth_Read();

        // Finally set style and icons
        SetStyle();
        InitializeImageList();  // must be here - if called in constructor, it won't work

        _isInitialized = true;
    }

    /// <summary>
    /// Can be called to make the FileListView persist its user
    /// settings (like column order and width).
    /// </summary>
    public void PersistSettings()
    {
        ColOrderAndWidth_Write();
    }

    #endregion


    /// <summary>
    /// Returns the number of items in the list view that actually
    /// have an entry in the given column.
    /// </summary>
    /// <param name="column">The column to check for values</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException">If the column was not found</exception>
    public int CountItemsWithData(string column)
    {
        int colIndex = GetColumnIndex(column);
        int itemCount = 0;

        foreach (ListViewItem lvi in Items)
        {
            if (lvi.SubItems.Count > 1)
            {
                if (lvi.SubItems[index: colIndex].Text
                        .Replace(oldValue: UNKNOWN_VALUE_FILE, newValue: "")
                        != "")
                {
                    itemCount++;
                }
            }
        }
        return itemCount;
    }


    /// <summary>
    /// Returns the (sub items) index of the column (not the visualized one).
    /// </summary>
    /// <param name="column">The column to look for w/o prefix</param>
    /// <returns>The index</returns>
    /// <exception cref="ArgumentException">If the column was not found</exception>
    public int GetColumnIndex(string column) {
        if (!_cfg_Col_Names.Contains(column)) throw new ArgumentException("Column with name '" + column + "' not found.");

        return this.Columns[COL_NAME_PREFIX + column].Index;
    }


    #region Modes

    /// <summary>
    ///     Suspends sorting the list view
    /// </summary>
    public void SuspendColumnSorting()
    {
        ListViewItemSorter = null;
    }

    /// <summary>
    ///     Resume sorting the list view
    /// </summary>
    public void ResumeColumnSorting()
    {
        ListViewItemSorter = LvwColumnSorter;
    }

    #endregion


    #region Updating

    /// <summary>
    /// Restaff the list view with the set of directory elements handed.
    /// 
    /// Note that the DirectoryElementCollection is assumed to be
    /// in scope of the FileListView. Calling FileListView.Clear will
    /// also clear it.
    /// </summary>
    public void ReloadFromDEs(DirectoryElementCollection directoryElements)
    {
        // Temp. disable sorting of the list view
        Logger.Trace(message: "Disable ListViewItemSorter");
        SuspendColumnSorting();

        DirectoryElements = directoryElements;
        _fileCount = 0;
        foreach (DirectoryElement item in DirectoryElements)
        {
            addListItem(item: item);
            if (item.Type == DirectoryElement.ElementType.File) _fileCount++;
        }

        // Resume sorting...
        Logger.Trace(message: "Enable ListViewItemSorter");
        ResumeColumnSorting();
        Sort();
    }


    /// <summary>
    /// Clears the FileListView.
    /// 
    /// Should be used instead Items.Clear, etc. as it correctly handles
    /// all due other things to do for clearing, like clearing the
    /// the Directory Elements collection.
    /// </summary>
    public void ClearData()
    {
        Items.Clear();
        DirectoryElements.Clear();
    }


    /// <summary>
    ///     Scrolls to the relevant line of the listview
    /// </summary>
    /// <param name="itemText">The particular ListViewItem (by text) that needs updating</param>
    public void ScrollToDataPoint(string itemText)
    {
        // If the current thread is not the UI thread, InvokeRequired will be true
        if (InvokeRequired)
        {
            Invoke(method: (Action)(() => ScrollToDataPoint(itemText: itemText)));
            return;
        }

        ListViewItem itemToModify = FindItemWithText(text: itemText);
        if (itemToModify != null)
        {
            EnsureVisible(index: itemToModify.Index);
        }
    }


    /// <summary>
    ///     Deals with invoking the listview (from outside the thread) and updating the colour of a particular row (Item) to
    ///     the assigned colour.
    /// </summary>
    /// <param name="lvw">The listView Control that needs updating. Most likely the one in the main Form</param>
    /// <param name="itemText">The particular ListViewItem (by text) that needs updating</param>
    /// <param name="color">Parameter to assign a particular colour (prob red or black) to the whole row</param>
    public void UpdateItemColour(string itemText,
                                 Color color)
    {
        // If the current thread is not the UI thread, InvokeRequired will be true
        if (InvokeRequired)
        {
            Invoke(method: (Action)(() => UpdateItemColour(itemText: itemText, color: color)));
            return;
        }

        ListViewItem itemToModify = FindItemWithText(text: itemText);
        if (itemToModify != null)
        {
            itemToModify.ForeColor = color;
        }
    }

    #endregion


    #region Handlers

    /// <summary>
    ///     Handles the sorting and reordering.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void FileList_ColumnClick(object sender,
                                      ColumnClickEventArgs e)
    {
        if (e.Column == LvwColumnSorter.SortColumn)
        {
            // Column clicked is current sort column --> Reverse order
            if (LvwColumnSorter.SortOrder == SortOrder.Ascending)
            {
                LvwColumnSorter.SortOrder = SortOrder.Descending;
            }
            else
            {
                LvwColumnSorter.SortOrder = SortOrder.Ascending;
            }
        }
        else
        {
            LvwColumnSorter.SortColumn = e.Column;
            LvwColumnSorter.SortOrder = SortOrder.Ascending;
        }

        // Perform the sort with these new sort options.
        Sort();
    }

    private void FileList_ColumnWidthChanging(object sender,
                                              ColumnWidthChangingEventArgs e)
    {
        // Columns with width = 0 should stay hidden / may not be resized.
        if (Columns[index: e.ColumnIndex]
                .Width ==
            0)
        {
            e.Cancel = true;
            e.NewWidth = 0;
        }
    }

    private void FileList_ColumnReordered(object sender,
                                          ColumnReorderedEventArgs e)
    {
        // Prevent FileName column to be moved
        if (e.Header.Index == 0)
        {
            e.Cancel = true;
        }
    }

    #endregion
}