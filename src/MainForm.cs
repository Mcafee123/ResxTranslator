﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

using ResxTranslator.Properties;

namespace ResxTranslator
{
    public partial class MainForm : Form
    {
        private string _exportPath = "C:\\Temp";

        private static int FindCheckedSubItemIndex(ToolStripMenuItem autoTranslate)
        {
            for (int index = 0; index < autoTranslate.DropDownItems.Count; index++)
            {
                var item = (autoTranslate.DropDownItems[index] as ToolStripMenuItem);
                if (item.Checked)
                {
                    return index;
                }
            }
            return -1;
        }

        private static void SaveResourceHolder(ResourceHolder resource)
        {
            try
            {
                if (!resource.IsDirty)
                {
                    return;
                }

                resource.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Exception while saving: " + resource.Id);
            }
        }

        protected ResourceHolder CurrentResource;
        protected Thread DictBuilderThread;

        protected Dictionary<string, int> LanguagesInUse = new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase);

        protected int LastClickedLanguageIndex;
        protected readonly Dictionary<string, ResourceHolder> Resources;

        protected string RootPath;
        protected Thread ToUppercaseThread;
        protected Thread AutoTranslationThread;

        private SearchParams _currentSearch;
        private DateTime _mouseDownStart = DateTime.MaxValue;

        private volatile bool _requestDictBuilderStop;
        private bool dndInProgress = false;

        public MainForm()
        {
            this.InitializeComponent();

            this.Resources = new Dictionary<string, ResourceHolder>();
            this.labelTitle.Visible = false;
        }

        public SearchParams CurrentSearch
        {
            get { return this._currentSearch; }
            set
            {
                this._currentSearch = value;
                this.ExecuteFind();
            }
        }

        public bool RequestDictBuilderStop
        {
            get { return this._requestDictBuilderStop; }
            set { this._requestDictBuilderStop = value; }
        }

        public bool RequestToUppercaseStop { get; set; }
        public bool RequestAutoTranslationStop { get; set; }

        private void ApplyConditionalFormatting()
        {
            foreach (DataGridViewRow r in this.dataGridView1.Rows)
            {
                this.ApplyConditionalFormatting(r);
            }
        }

        private void ApplyConditionalFormatting(DataGridViewRow r)
        {
            if (r.Cells["Error"].Value != null && (bool)r.Cells["Error"].Value)
            {
                r.DefaultCellStyle.ForeColor = Color.Red;
            }
            else
            {
                r.DefaultCellStyle.ForeColor = this.dataGridView1.DefaultCellStyle.ForeColor;
            }
        }

        private void ApplyFilterCondition()
        {
            if (this.dataGridView1.DataSource == null)
            {
                return;
            }

            ((DataTable)this.dataGridView1.DataSource).DefaultView.RowFilter = this.hideNontranslatedToolStripMenuItem.Checked ? " Translated = 1" : "";
        }

        private void BuildTreeView(ResourceHolder resource)
        {
            TreeNode parentNode = null;
            string[] topFolders = resource.DisplayFolder.Split('\\');
            foreach (string subFolder in topFolders)
            {
                TreeNodeCollection searchNodes = parentNode == null ? this.treeViewResx.Nodes : parentNode.Nodes;
                bool found = false;
                foreach (TreeNode treeNode in searchNodes)
                {
                    if (treeNode.Tag is PathHolder && (treeNode.Tag as PathHolder).Id.Equals(subFolder, StringComparison.InvariantCultureIgnoreCase))
                    {
                        found = true;
                        parentNode = treeNode;
                        break;
                    }
                }
                if (!found)
                {
                    var pathTreeNode = new TreeNode("[" + subFolder + "]");
                    var pathHolder = new PathHolder();
                    pathHolder.Id = subFolder;
                    pathTreeNode.Tag = pathHolder;
                    searchNodes.Add(pathTreeNode);

                    parentNode = pathTreeNode;
                }
            }

            var leafNode = new TreeNode(resource.Id);
            leafNode.Tag = resource;

            resource.DirtyChanged += delegate { this.SetTreeNodeDirty(leafNode, resource); };

            this.SetTreeNodeTitle(leafNode, resource);

            resource.LanguageChange += delegate { this.SetTreeNodeTitle(leafNode, resource); };

            if (parentNode != null)
            {
                parentNode.Nodes.Add(leafNode);
            }
        }

        /// <summary>
        ///     Check and prompt for save
        /// </summary>
        /// <returns>True if we can safely close</returns>
        private bool CanClose()
        {
            bool isDirty = this.Resources.Values.Any(resource => resource.IsDirty);

            if (isDirty)
            {
                DialogResult dialogResult = MessageBox.Show("Do you want save your changes before closing?", "Save Changes", MessageBoxButtons.YesNoCancel);

                if (dialogResult == DialogResult.Yes)
                {
                    this.StopDictBuilderThread();
                    this.SaveAll();
                    return true;
                }
                else if (dialogResult == DialogResult.No)
                {
                    return true;
                }

                this.StopDictBuilderThread();
                return false;
            }
            this.StopDictBuilderThread();

            return true;
        }

        private void ExecuteFind()
        {
            this.ExecuteFindInNodes(this.treeViewResx.Nodes);
        }

        private void ExecuteFindInNodes(TreeNodeCollection searchNodes)
        {
            Color matchColor = Color.GreenYellow;
            foreach (TreeNode treeNode in searchNodes)
            {
                treeNode.BackColor = Color.White;
                this.ExecuteFindInNodes(treeNode.Nodes);
                if (treeNode.Tag is ResourceHolder)
                {
                    var resource = treeNode.Tag as ResourceHolder;
                    if (this.CurrentSearch.Match(SearchParams.TargetType.Lang, resource.NoLanguageLanguage))
                    {
                        treeNode.BackColor = matchColor;
                    }
                    string[] file = resource.Filename.Split('\\');

                    if (this.CurrentSearch.Match(SearchParams.TargetType.File, file[file.Length - 1]))
                    {
                        treeNode.BackColor = matchColor;
                    }
                    foreach (LanguageHolder lng in resource.Languages.Values)
                    {
                        if (this.CurrentSearch.Match(SearchParams.TargetType.Lang, lng.Id))
                        {
                            treeNode.BackColor = matchColor;
                        }
                    }
                    foreach (DataRow row in resource.StringsTable.Rows)
                    {
                        if (this.CurrentSearch.Match(SearchParams.TargetType.Key, row["Key"].ToString()))
                        {
                            treeNode.BackColor = matchColor;
                        }
                        if (this.CurrentSearch.Match(SearchParams.TargetType.Text, row["NoLanguageValue"].ToString()))
                        {
                            treeNode.BackColor = matchColor;
                        }
                        foreach (LanguageHolder lng in resource.Languages.Values)
                        {
                            if (this.CurrentSearch.Match(SearchParams.TargetType.Text, row[lng.Id].ToString()))
                            {
                                treeNode.BackColor = matchColor;
                            }
                        }
                    }
                }
            }
        }

        private void FindResx(string folder)
        {
            string displayFolder = "";
            if (folder.StartsWith(this.RootPath, StringComparison.InvariantCultureIgnoreCase))
            {
                displayFolder = folder.Substring(this.RootPath.Length);
            }
            if (displayFolder.StartsWith("\\"))
            {
                displayFolder = displayFolder.Remove(0, 1);
            }

            string[] files = Directory.GetFiles(folder, "*.resx");

            foreach (string file in files)
            {
                string filenameNoExt = "" + Path.GetFileNameWithoutExtension(file);
                string[] fileParts = filenameNoExt.Split('.');
                if (fileParts.Length == 0)
                {
                    continue;
                }

                string language = "";
                if (fileParts[fileParts.Length - 1].Length == 5 && fileParts[fileParts.Length - 1][2] == '-')
                {
                    language = fileParts[fileParts.Length - 1];
                }
                else if (fileParts[fileParts.Length - 1].Length == 2)
                {
                    language = fileParts[fileParts.Length - 1];
                }
                if (!string.IsNullOrEmpty(language))
                {
                    filenameNoExt = Path.GetFileNameWithoutExtension(filenameNoExt);
                }

                ResourceHolder resourceHolder;
                string key = (displayFolder + "\\" + filenameNoExt).ToLower();
                if (!this.Resources.TryGetValue(key, out resourceHolder))
                {
                    resourceHolder = new ResourceHolder();
                    resourceHolder.DisplayFolder = displayFolder;
                    if (string.IsNullOrEmpty(language))
                    {
                        resourceHolder.Filename = file;
                    }
                    resourceHolder.Id = filenameNoExt;

                    this.Resources.Add(key, resourceHolder);
                }

                if (!string.IsNullOrEmpty(language))
                {
                    if (!this.LanguagesInUse.ContainsKey(language))
                    {
                        this.LanguagesInUse[language] = 0;
                    }
                    this.LanguagesInUse[language] += 1;
                    if (!resourceHolder.Languages.ContainsKey(language.ToLower()))
                    {
                        var languageHolder = new LanguageHolder();
                        languageHolder.Filename = file;
                        languageHolder.Id = language;
                        resourceHolder.Languages.Add(language.ToLower(), languageHolder);
                    }
                }
                else
                {
                    resourceHolder.Filename = file;
                }
            }

            string[] subfolders = Directory.GetDirectories(folder);
            foreach (string subfolder in subfolders)
            {
                this.FindResx(subfolder);
            }
        }

        private void OpenProject(string selectedPath)
        {
            this.StopDictBuilderThread();

            this.toolStripStatusLabel1.Text = "Building tree";
            this.RootPath = selectedPath;

            Settings.Default.Mrud = this.RootPath;
            Settings.Default.Save();

            this.FindResx(this.RootPath);

            this.treeViewResx.Nodes.Clear();
            foreach (ResourceHolder resource in this.Resources.Values)
            {
                this.BuildTreeView(resource);
            }

            this.treeViewResx.ExpandAll();
            this.addLanguageToolStripMenuItem.DropDownItems.Clear();
            foreach (string s in this.LanguagesInUse.Keys)
            {
                this.addLanguageToolStripMenuItem.DropDownItems.Add(s);
            }
            this.toolStripStatusLabel1.Text = "Building local dictionary";
            this.toolStripProgressBar1.Visible = true;
            this.toolStripProgressBar1.Maximum = this.Resources.Count();
            this.toolStripProgressBar1.Value = this.Resources.Count() / 50; //make it green a little..

            this.StartDictBuilderThread();
        }

        private void SaveAll()
        {
            foreach (ResourceHolder resource in this.Resources.Values)
            {
                SaveResourceHolder(resource);
            }
        }

        private void SelectResourceFromTree()
        {
            TreeNode selectedTreeNode = this.treeViewResx.SelectedNode;
            if (selectedTreeNode == null)
            {
                return;
            }

            if (selectedTreeNode.Tag is PathHolder)
            {
                return;
            }

            if (!(selectedTreeNode.Tag is ResourceHolder))
            {
                // Shouldn't happen
                return;
            }

            var resource = selectedTreeNode.Tag as ResourceHolder;

            this.ShowResourceInGrid(resource);
        }

        private void SetLanguageColumnVisible(string languageId, bool visible)
        {
            if (this.dataGridView1.Columns.Contains(languageId))
            {
                this.dataGridView1.Columns[languageId].Visible = visible;
            }
        }

        public void SetTranslationAvailable(bool isIt)
        {
            this.translateUsingBingToolStripMenuItem.Enabled = isIt;
            this.autoTranslateToolStripMenuItem1.Enabled = isIt;
            this.autoTranslateThisCellToolStripMenuItem.Enabled = isIt;
        }

        public void SetTreeNodeDirty(TreeNode node, ResourceHolder res)
        {
            this.InvokeIfRequired(c => { node.ForeColor = res.IsDirty ? Color.Blue : Color.Black; });
        }

        public void SetTreeNodeTitle(TreeNode node, ResourceHolder res)
        {
            this.InvokeIfRequired(c => { node.Text = res.Caption; });
        }

        private void ShowResourceInGrid(ResourceHolder resource)
        {
            this.CurrentResource = resource;

            this.labelTitle.Text = resource.Id;
            this.labelTitle.Visible = true;

            this.dataGridView1.DataSource = resource.StringsTable;

            this.checkedListBoxLanguages.Items.Clear();

            foreach (LanguageHolder languageHolder in resource.Languages.Values)
            {
                this.checkedListBoxLanguages.Items.Add(languageHolder, true);
                this.dataGridView1.Columns[languageHolder.Id].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            }

            this.dataGridView1.Columns["NoLanguageValue"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            this.dataGridView1.Columns["Comment"].DisplayIndex = this.dataGridView1.Columns.Count - 1;

            this.dataGridView1.Columns["Translated"].Visible = false;
            this.dataGridView1.Columns["Error"].Visible = false;

            this.ApplyFilterCondition();

            this.dataGridView1.Columns["Key"].ReadOnly = true;

            this.ApplyConditionalFormatting();
        }

        private void StartDictBuilderThread()
        {
            // Make the logic for building the dictionary an anonymous delegate to keep it only callable on the separate thread
            var buildDictionary = (ThreadStart)delegate
                                               {
                                                   #region Dictionary building loop (long)

                                                   int rescount = 0;
                                                   this.InvokeIfRequired(c =>
                                                                         {
                                                                             c.toolStripStatusLabel1.Text = "Building language lookup";
                                                                             c.toolStripProgressBar1.Value = 0;
                                                                             c.toolStripStatusLabelCurrentItem.Text = "";
                                                                         });

                                                   foreach (ResourceHolder res in this.Resources.Values)
                                                   {
                                                       if (this.RequestDictBuilderStop)
                                                       {
                                                           break;
                                                       }
                                                       ResourceHolder res1 = res;
                                                       this.InvokeIfRequired(c => { c.toolStripStatusLabelCurrentItem.Text = res1.Filename; });

                                                       var translator = InprojectTranslator.Instance;

                                                       foreach (string lang in res.Languages.Keys)
                                                       {
                                                           StringBuilder sbAllNontranslated = new StringBuilder();
                                                           StringBuilder sbAllTranslated = new StringBuilder();
                                                           foreach (DataRow row in res.StringsTable.Rows)
                                                           {
                                                               sbAllNontranslated.Append(row["NoLanguageValue"].ToString());
                                                               sbAllNontranslated.Append(" ");

                                                               if (row[lang.ToLower()] != DBNull.Value && row[lang.ToLower()].ToString().Trim() != "")
                                                               {
                                                                   sbAllTranslated.Append(row[lang.ToLower()].ToString().Trim());
                                                                   sbAllTranslated.Append(" ");
                                                               }
                                                           }
                                                           var diffArray = translator.RemoveWords(sbAllNontranslated.ToString(), sbAllTranslated.ToString());
                                                           translator.AddWordsToLanguageChecker(lang.ToLower(), diffArray);
                                                       }
                                                       ++rescount;
                                                       int rescount1 = rescount;
                                                       this.InvokeIfRequired(c => { c.toolStripProgressBar1.Value = rescount1; });
                                                   }
                                                   this.InvokeIfRequired(c =>
                                                                         {
                                                                             c.toolStripStatusLabel1.Text = "Building local translations dictionary";
                                                                             c.toolStripProgressBar1.Value = this.Resources.Count / 50;
                                                                             c.toolStripStatusLabelCurrentItem.Text = "";
                                                                         });

                                                   rescount = 0;
                                                   foreach (ResourceHolder res in this.Resources.Values)
                                                   {
                                                       if (this.RequestDictBuilderStop)
                                                       {
                                                           break;
                                                       }
                                                       ResourceHolder res1 = res;
                                                       this.InvokeIfRequired(c => { c.toolStripStatusLabelCurrentItem.Text = res1.Filename; });

                                                       string resDeflang = res.NoLanguageLanguage;
                                                       var sb = new StringBuilder();
                                                       foreach (DataRow row in res.StringsTable.Rows)
                                                       {
                                                           string nontranslated = row["NoLanguageValue"].ToString();
                                                           if (!string.IsNullOrEmpty(nontranslated) && nontranslated.Trim() != "")
                                                           {
                                                               foreach (string lang in res.Languages.Keys)
                                                               {
                                                                   if (row[lang.ToLower()] != DBNull.Value && row[lang.ToLower()].ToString().Trim() != "")
                                                                   {
                                                                       sb.Append(" ");
                                                                       sb.Append(row[lang.ToLower()].ToString());

                                                                       InprojectTranslator.Instance.AddTranslation(resDeflang, nontranslated, lang.ToLower(), row[lang.ToLower()].ToString().Trim());
                                                                       InprojectTranslator.Instance.AddTranslation(lang.ToLower(), row[lang.ToLower()].ToString().Trim(), resDeflang, nontranslated);
                                                                   }
                                                               }
                                                           }
                                                           if (resDeflang != "")
                                                           {
                                                               InprojectTranslator.Instance.AddWordsToLanguageChecker(resDeflang, InprojectTranslator.Instance.RemoveWords(sb.ToString(), nontranslated));
                                                           }
                                                       }
                                                       ++rescount;
                                                       int rescount1 = rescount;
                                                       this.InvokeIfRequired(c => { c.toolStripProgressBar1.Value = rescount1; });
                                                   }
                                                   this.InvokeIfRequired(c =>
                                                                         {
                                                                             c.toolStripStatusLabel1.Text = "Done";
                                                                             c.toolStripProgressBar1.Visible = false;
                                                                             c.toolStripStatusLabelCurrentItem.Text = "";
                                                                         });

                                                   #endregion
                                               };

            this.DictBuilderThread = new Thread(buildDictionary);
            this.DictBuilderThread.Name = "DictBuilder";
            this.RequestDictBuilderStop = false;

            this.DictBuilderThread.Start();
        }

        private void StartAutoTranslationThread()
        {
            var autoTranslation = (ThreadStart)delegate
                                               {
                                                   var max = Resources.Values.Count;
                                                   this.InvokeIfRequired(c =>
                                                                         {
                                                                             c.toolStripStatusLabel1.Text = "change all suffizes to Uppercase";
                                                                             c.toolStripProgressBar1.Value = 0;
                                                                             c.toolStripProgressBar1.Maximum = max;
                                                                         });

                                                   foreach (var resource in this.Resources.Values)
                                                   {
                                                       foreach (DataRow dr in resource.StringsTable.Rows)
                                                       {
                                                           var noLanguageValue = dr["NoLanguageValue"].ToString();
                                                           if (!String.IsNullOrEmpty(noLanguageValue))
                                                           {
                                                               var allVal = new List<string> { noLanguageValue };
                                                               // check for empty tranlsations
                                                               foreach (var lang in resource.Languages.Keys)
                                                               {
                                                                   var translation = dr[lang].ToString();
                                                                   allVal.Add(translation);
                                                                   if (String.IsNullOrEmpty(translation))
                                                                   {
                                                                       dr[lang] = String.Format("{0} ({1})", noLanguageValue, lang.ToUpper());
                                                                   }
                                                               }
                                                               // check if all values are the same
                                                               if (allVal.Distinct().Count() == 1 && !FilterByKey(dr["Key"].ToString()) && !FilterByValue(allVal[0]))
                                                               {
                                                                   // if yes, add suffix
                                                                   foreach (var lang in resource.Languages.Keys)
                                                                   {
                                                                       dr[lang] = String.Format("{0} ({1})", noLanguageValue, lang.ToUpper());
                                                                   }
                                                               }
                                                           }
                                                       }
                                                       this.InvokeIfRequired(c=> c.toolStripProgressBar1.Value += 1);
                                                   }
                                                   this.InvokeIfRequired(c =>
                                                   {
                                                       c.toolStripProgressBar1.Value = 0;
                                                       c.toolStripStatusLabel1.Text = "Done";
                                                       c.toolStripProgressBar1.Visible = false;
                                                       c.toolStripStatusLabelCurrentItem.Text = "";
                                                       MessageBox.Show("finished");
                                                   });
                                               };
            AutoTranslationThread = new Thread(autoTranslation);
            AutoTranslationThread.Name = "AutoTranslation";
            RequestAutoTranslationStop = false;
            AutoTranslationThread.Start();
        }

        private bool FilterByKey(string key)
        {
            return Settings.Default.ignoreByKey.Split(',').Contains(key);
        }


        private bool FilterByValue(string inputString)
        {
            var exceptions = Settings.Default.ignoreWhenAllTheSame.Split(',').ToList();
            return exceptions.Contains(inputString);
        }

        private void StartToUppercaseThread()
        {
            // Make the logic for building the dictionary an anonymous delegate to keep it only callable on the separate thread
            var toUppercase = (ThreadStart)delegate
                                           {
                                               var rx = new Regex("(\\(|\\[)(?<kuerzel>it|fr)(\\)|\\])");
                                               this.InvokeIfRequired(c=>
                                                                     { c.toolStripStatusLabel1.Text = "change all suffizes to Uppercase"; });
                                               foreach (var resource in this.Resources.Values)
                                               {
                                                   var fileName = resource.Filename;
                                                   var max = resource.StringsTable.Rows.Count;
                                                   this.InvokeIfRequired(c =>
                                                   {
                                                       c.toolStripStatusLabelCurrentItem.Text = String.Format("Checking {0}", fileName);
                                                       c.toolStripProgressBar1.Value = 0;
                                                       c.toolStripProgressBar1.Maximum = max;
                                                   });

                                                   foreach (DataRow dr in resource.StringsTable.Rows)
                                                   {
                                                       var key = dr["Key"].ToString();
                                                       this.InvokeIfRequired(c =>
                                                                             { 
                                                                                 //c.toolStripStatusLabelCurrentItem.Text = key;
                                                                                 c.toolStripProgressBar1.Value += 1;
                                                                             });
                                                       foreach (var lang in resource.Languages.Keys)
                                                       {
                                                           var val = dr[lang].ToString();
                                                           if (!String.IsNullOrEmpty(val))
                                                           {
                                                               Match ma = rx.Match(val);
                                                               if (ma.Success)
                                                               {
                                                                   var kuerzel = ma.Groups["kuerzel"].Value.ToUpper();
                                                                   val = val.Replace(ma.Value, String.Format("({0})", kuerzel));
                                                               }
                                                               if (dr[lang].ToString() != val.Trim())
                                                               {
                                                                   dr[lang] = val.Trim();
                                                               }
                                                           }
                                                       }
                                                   }
                                               }
                                               this.InvokeIfRequired(c =>
                                                                     {
                                                                         c.toolStripProgressBar1.Value = 0;
                                                                         c.toolStripStatusLabel1.Text = "Done";
                                                                         c.toolStripProgressBar1.Visible = false;
                                                                         c.toolStripStatusLabelCurrentItem.Text = "";
                                                                         MessageBox.Show("finished");
                                                                     });
                                           };

            ToUppercaseThread = new Thread(toUppercase);
            ToUppercaseThread.Name = "ToUppercase";
            RequestToUppercaseStop = false;
            ToUppercaseThread.Start();
        }

        private void StopDictBuilderThread()
        {
            if (this.DictBuilderThread != null && this.DictBuilderThread.IsAlive)
            {
                this.RequestDictBuilderStop = true;
                while (false == this.DictBuilderThread.Join(50))
                {}
            }
            this.RequestDictBuilderStop = false;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!this.CanClose())
            {
                e.Cancel = true;
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            this.SetTranslationAvailable(!string.IsNullOrEmpty(Settings.Default.BingAppId));

            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 2 && args[1].Trim() == "-f" && !string.IsNullOrEmpty(args[2]))
            {
                string path = args[2].Trim();
                if (path.Contains("\""))
                {
                    path = path.Replace("\"", "").Trim();
                }
                try
                {
                    DirectoryInfo fldr = new DirectoryInfo(path);
                    if (!fldr.Exists)
                    {
                        throw new ArgumentException("Folder '" + path + "' does not exist.");
                    }
                    path = (fldr.FullName + "\\").Replace("\\\\", "\\");
                    this.OpenProject(path);
                }
                catch (Exception inner)
                {
                    throw new ArgumentException("Invalid command line \r\n" + Environment.CommandLine + "\r\nPath: " + path, inner);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(Settings.Default.BingAppId))
                {
                    //MessageBox.Show("Note! to use auto translate you need to get a Bing AppID.", "ResxTranslator");
                }
            }
        }

        private void addLanguageToolStripMenuItem_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            this.CurrentResource.AddLanguage(e.ClickedItem.Text);
            this.ShowResourceInGrid(this.CurrentResource);
        }

        private void addNewKeyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.CurrentResource == null)
            {
                return;
            }

            using (var form = new AddKey(this.CurrentResource))
            {
                DialogResult result = form.ShowDialog();

                if (result == DialogResult.OK)
                {
                    // Add key
                    this.CurrentResource.AddString(form.Key, form.NoXlateValue, form.DefaultValue);
                }
            }
        }

        private void allFilledItemsToUppercaseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.toolStripStatusLabel1.Text = "Building local dictionary";
            this.toolStripProgressBar1.Visible = true;
            this.toolStripProgressBar1.Maximum = this.Resources.Count();
            this.toolStripProgressBar1.Value = this.Resources.Count() / 50; //make it green a little..

            this.StartToUppercaseThread();
        }

        private void autoTranslateThisCellToolStripMenuItem_Click(object sender, EventArgs e)
        {
            int colIndex = this.dataGridView1.CurrentCell.ColumnIndex;
            string column = this.dataGridView1.Columns[colIndex].Name;
            string source = this.dataGridView1.CurrentCell.Value.ToString();

            var autoTranslate = this.contextMenuStripCell.Items["autoTranslateThisCellToolStripMenuItem"] as ToolStripMenuItem;

            var preferred = "NoLanguageValue";
            if (!(autoTranslate.DropDownItems[1] as ToolStripMenuItem).Checked)
            {
                int subChk = FindCheckedSubItemIndex(autoTranslate);
                if (subChk > -1)
                {
                    preferred = autoTranslate.DropDownItems[subChk].Text;
                }
                else
                {
                    preferred = Settings.Default.PreferredSourceLanguage;
                }
            }

            if (string.IsNullOrEmpty(source.Trim()))
            {
                source = this.dataGridView1.Rows[this.dataGridView1.CurrentCell.RowIndex].Cells[preferred].Value.ToString();
            }
            if (column == "NoLanguageValue")
            {
                column = this.CurrentResource.NoLanguageLanguage;
            }

            string translation = BingTranslator.TranslateString(source, column);
            this.dataGridView1.CurrentCell.Value = translation;
            this.dataGridView1.EndEdit();
        }

        private void autoTranslateThisCellToolStripMenuItem_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            var checkedItem = e.ClickedItem as ToolStripMenuItem;
            var autoTranslate = this.contextMenuStripCell.Items["autoTranslateThisCellToolStripMenuItem"] as ToolStripMenuItem;

            foreach (ToolStripMenuItem item in autoTranslate.DropDownItems)
            {
                item.Checked = false;
            }
            checkedItem.Checked = true;
            var preferred = ("" + checkedItem.Tag) == "NoLanguageValue" ? "NoLanguageValue" : checkedItem.Text;

            Settings.Default.PreferredSourceLanguage = preferred;

            int colIndex = this.dataGridView1.CurrentCell.ColumnIndex;
            string column = this.dataGridView1.Columns[colIndex].Name;
            if (column == "NoLanguageValue")
            {
                column = this.CurrentResource.NoLanguageLanguage;
            }
            string source = this.dataGridView1.Rows[this.dataGridView1.CurrentCell.RowIndex].Cells[preferred].Value.ToString();

            string translation = BingTranslator.TranslateString(source, column);
            this.dataGridView1.CurrentCell.Value = translation;
            this.dataGridView1.EndEdit();
        }

        private void autoTranslateToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            var myItem = sender as ToolStripMenuItem;
            if (myItem != null)
            {
                //Get the ContextMenuString (owner of the ToolsStripMenuItem)
                var theStrip = myItem.Owner as ContextMenuStrip;
                if (theStrip != null)
                {
                    //The SourceControl is the control that opened the contextmenustrip.
                    //In my case it could be a linkLabel
                    var box = theStrip.SourceControl as CheckedListBox;
                    if (box != null)
                    {
                        BingTranslator.AutoTranslate(this.CurrentResource, box.Items[this.LastClickedLanguageIndex].ToString());
                    }
                }
            }
        }

        private void checkedListBoxLanguages_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            var languageHolder = this.checkedListBoxLanguages.Items[e.Index] as LanguageHolder;
            if (languageHolder == null)
            {
                return;
            }

            if (this.dataGridView1.DataSource == null)
            {
                // Not populated yet
                return;
            }

            this.SetLanguageColumnVisible(languageHolder.Id, e.NewValue == CheckState.Checked);
        }

        private void checkedListBoxLanguages_MouseDown(object sender, MouseEventArgs e)
        {
            this.LastClickedLanguageIndex = this.checkedListBoxLanguages.IndexFromPoint(e.Location);
        }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!this.CanClose())
            {
                return;
            }

            this.treeViewResx.Nodes.Clear();
            this.checkedListBoxLanguages.Items.Clear();
            this.labelTitle.Visible = false;

            this.CurrentResource = null;
            Settings.Default.Save();
        }

        private void dataGridView1_CellContextMenuStripNeeded(object sender, DataGridViewCellContextMenuStripNeededEventArgs e)
        {
            if (e.ColumnIndex < 0)
            {
                return;
            }
            e.ContextMenuStrip = this.contextMenuStripCell;

            var autoTranslate = this.contextMenuStripCell.Items["autoTranslateThisCellToolStripMenuItem"] as ToolStripMenuItem;

            if (autoTranslate.DropDownItems.Count < 3)
            {
                //rebuild the language select drop down
                int subChk = FindCheckedSubItemIndex(autoTranslate);
                string chkedLang = "";
                if (subChk > -1)
                {
                    chkedLang = autoTranslate.DropDownItems[subChk].Text;
                }
                else
                {
                    (autoTranslate.DropDownItems[0] as ToolStripMenuItem).Checked = true;
                }

                for (int i = autoTranslate.DropDownItems.Count - 1; i > 1; --i)
                {
                    autoTranslate.DropDownItems.RemoveAt(i);
                }

                foreach (var lang in this.CurrentResource.Languages.Keys)
                {
                    autoTranslate.DropDownItems.Add(lang);
                    var newItem = (autoTranslate.DropDownItems[autoTranslate.DropDownItems.Count - 1] as ToolStripMenuItem);
                    if (chkedLang == lang)
                    {
                        newItem.Checked = true;
                    }
                }
            }

            var preferred = "NoLanguageValue";
            if (!(autoTranslate.DropDownItems[1] as ToolStripMenuItem).Checked)
            {
                int subChk = FindCheckedSubItemIndex(autoTranslate);
                if (subChk > -1)
                {
                    preferred = autoTranslate.DropDownItems[subChk].Text;
                }
            }

            this.dataGridView1.CurrentCell = this.dataGridView1.Rows[e.RowIndex].Cells[e.ColumnIndex];
            string source = this.dataGridView1.Rows[this.dataGridView1.CurrentCell.RowIndex].Cells[preferred].Value.ToString();
            int colIndex = this.dataGridView1.CurrentCell.ColumnIndex;
            string column = this.dataGridView1.Columns[colIndex].Name;

            List<string> listofalternatives = InprojectTranslator.Instance.GetTranslations(this.CurrentResource.NoLanguageLanguage, source, column);
            for (int i = e.ContextMenuStrip.Items.Count - 1; i > 0; --i)
            {
                if (e.ContextMenuStrip.Items[i].Name.StartsWith("Transl"))
                {
                    e.ContextMenuStrip.Items.RemoveAt(i);
                }
            }
            foreach (string alt in listofalternatives)
            {
                e.ContextMenuStrip.Items.Add(alt);
                ToolStripItem newItem = e.ContextMenuStrip.Items[e.ContextMenuStrip.Items.Count - 1];
                string translation = alt;
                DataGridViewCell cell = this.dataGridView1.CurrentCell;
                newItem.Click += (EventHandler)delegate
                                               {
                                                   cell.Value = translation;
                                                   this.dataGridView1.EndEdit();
                                               };
                newItem.Name = "Transl_" + alt;
            }
        }

        private void dataGridView1_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (this.dataGridView1.RowCount == 0)
            {
                return;
            }
            if (this.dataGridView1.CurrentCell.IsInEditMode)
            {
                return;
            }

            var frm = new ZoomWindow();
            object value = this.dataGridView1.CurrentCell.Value;
            if (value == DBNull.Value)
            {
                frm.textBoxString.Text = "";
            }
            else
            {
                frm.textBoxString.Text = (string)value;
            }

            if (frm.ShowDialog() == DialogResult.OK)
            {
                this.dataGridView1.CurrentCell.Value = frm.textBoxString.Text;
                this.dataGridView1.EndEdit();
            }
        }

        private void dataGridView1_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            this._mouseDownStart = DateTime.Now;
        }

        private void dataGridView1_CellMouseLeave(object sender, DataGridViewCellEventArgs e)
        {
            if (DateTime.Now.Subtract(this._mouseDownStart).TotalMilliseconds > 50)
            {
                this._mouseDownStart = DateTime.MaxValue;
                if (e.RowIndex > -1 && e.ColumnIndex > 0)
                {
                    string text = this.dataGridView1.Rows[e.RowIndex].Cells[e.ColumnIndex].Value.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        this.dataGridView1.AllowDrop = true;
                        this.DoDragDrop(text, DragDropEffects.All);
                        this.dndInProgress = true;
                    }
                }
            }
        }

        private void dataGridView1_CellMouseUp(object sender, DataGridViewCellMouseEventArgs e)
        {
            this._mouseDownStart = DateTime.MaxValue;
            this.dataGridView1.AllowDrop = false;
        }

        private void dataGridView1_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            this.ApplyConditionalFormatting(this.dataGridView1.Rows[e.RowIndex]);
        }

        private void dataGridView1_DragDrop(object sender, DragEventArgs e)
        {
            Point p = this.dataGridView1.PointToClient(new Point(e.X, e.Y));
            DataGridView.HitTestInfo info = this.dataGridView1.HitTest(p.X, p.Y);
            Object value = e.Data.GetData(typeof(string));
            this.dataGridView1.AllowDrop = false;
            if (info.RowIndex != -1 && info.ColumnIndex != -1 && (ModifierKeys & Keys.Control) != 0)
            {
                if (value != null)
                {
                    this.dataGridView1.Rows[info.RowIndex].Cells[info.ColumnIndex].Value = value.ToString();
                }
            }
            this.dndInProgress = false;
        }

        private void dataGridView1_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.All;
        }

        private void dataGridView1_DragOver(object sender, DragEventArgs e)
        {
            if (this.dndInProgress)
            {
                if ((ModifierKeys & Keys.Control) != 0)
                {
                    e.Effect = DragDropEffects.Copy;
                }
                else
                {
                    e.Effect = DragDropEffects.None;
                }
            }
        }

        private void dataGridView1_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            ((DataGridViewTextBoxEditingControl)e.Control).AcceptsReturn = true;
            ((DataGridViewTextBoxEditingControl)e.Control).Multiline = true;
        }

        private void dataGridView1_UserAddedRow(object sender, DataGridViewRowEventArgs e)
        {}

        private void dataGridView1_UserDeletedRow(object sender, DataGridViewRowEventArgs e)
        {}

        private void deleteKeyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.CurrentResource == null || this.dataGridView1.RowCount == 0)
            {
                return;
            }

            if (MessageBox.Show("Are you sure you want to delete the current key?", "Delete", MessageBoxButtons.YesNoCancel) == DialogResult.Yes)
            {
                var dataRow = this.dataGridView1.SelectedRows[0].DataBoundItem as DataRowView;

                if (dataRow != null)
                {
                    dataRow.Row.Delete();
                }
            }
        }

        private void deleteLanguageFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var myItem = sender as ToolStripMenuItem;
            if (myItem != null)
            {
                //Get the ContextMenuString (owner of the ToolsStripMenuItem)
                var theStrip = myItem.Owner as ContextMenuStrip;
                if (theStrip != null)
                {
                    //The SourceControl is the control that opened the contextmenustrip.
                    var box = theStrip.SourceControl as CheckedListBox;
                    if (box != null)
                    {
                        if (MessageBox.Show("Do you really want to delete file for language " + box.Items[this.LastClickedLanguageIndex]) == DialogResult.OK)
                        {
                            this.CurrentResource.DeleteLanguage(box.Items[this.LastClickedLanguageIndex].ToString());
                            this.ShowResourceInGrid(this.CurrentResource);
                        }
                    }
                }
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void fillEmptyItemsToolStripMenuItem_Click(object sender, EventArgs e)
        {

            this.StartAutoTranslationThread();
        }

        private void findToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            var frm = new FindDialog();
            frm.ShowDialog(this);
        }

        private void hideNontranslatedToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.hideNontranslatedToolStripMenuItem.Checked = !this.hideNontranslatedToolStripMenuItem.Checked;

            this.ApplyFilterCondition();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!this.CanClose())
            {
                return;
            }

            var folderDialog = new FolderBrowserDialog();
            folderDialog.SelectedPath = Settings.Default.Mrud;
            folderDialog.Description = "Browse to the root of the project, typically where the sln file is";
            if (folderDialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            string selectedPath = folderDialog.SelectedPath;

            this.OpenProject(selectedPath);
        }

        private void revertCurrentToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.CurrentResource.Revert();
            this.ShowResourceInGrid(this.CurrentResource);
        }

        private void saveCurrentToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveResourceHolder(this.CurrentResource);
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Save
            SaveAll();
        }

        private void setBingAppIdToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var frm = new BingParams();
            frm.ShowDialog(this);
        }

        private void translateUsingBingToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (DialogResult.OK == MessageBox.Show("Do you want to autotranslate all non-translated texts for all languages in this resource?"))
            {
                this.CurrentResource.AutoTranslate();
            }
        }

        private void treeViewResx_AfterSelect(object sender, TreeViewEventArgs e)
        {
            this.SelectResourceFromTree();
        }

        private void treeViewResx_DoubleClick(object sender, EventArgs e)
        {
            this.SelectResourceFromTree();
        }

        private void exportRowsToTranslateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var folderBrowser = new FolderBrowserDialog();
            if (Directory.Exists(_exportPath))
            {
                folderBrowser.SelectedPath = _exportPath;
            }
            var result = folderBrowser.ShowDialog();
            if (result == DialogResult.OK)
            {
                try
                {
                    // create sortable list
                    var rows = new SortedList<string, string>();
                    foreach (var resource in this.Resources.Values)
                    {
                        // export
                        foreach (DataRow dr in resource.StringsTable.Rows)
                        {
                            var row = new ResourceRow(resource.DisplayFolder, resource.Filename, dr["Key"].ToString(), dr["NoLanguageValue"].ToString());
                            foreach (var culture in resource.Languages)
                            {
                                row.AddTranslation(culture.Key, dr[culture.Key].ToString());
                            }
                            if (row.HasOpenTranslations())
                            {
                                var key = CreateTranslationKey(dr, resource);
                                rows.Add(key, row.ToString());
                            }
                        }
                    }

                    var exportFilePath = Path.Combine(folderBrowser.SelectedPath, "translationExport.csv");
                    if (File.Exists(exportFilePath))
                    {
                        File.Delete(exportFilePath);
                    }
                    using (var textStream = new FileStream(exportFilePath, FileMode.CreateNew, FileAccess.Write))
                    {
                        using (var tw = new StreamWriter(textStream, Encoding.UTF8))
                        {
                            // headers
                            tw.WriteLine(ResourceRow.GetHeaderNames());
                            // write values
                            foreach (var key in rows.Keys)
                            {
                                tw.WriteLine(rows[key]);
                            }
                            tw.Close();
                        }

                    }
                    _exportPath = folderBrowser.SelectedPath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(String.Format("Error: {0}", ex.GetBaseException().Message));
                }
            }
        }

        private string CreateTranslationKey(DataRow dr, ResourceHolder resource)
        {
            return dr["Key"] + "_" + resource.Id;
        }
        
        private void cleanupRegisteredExtensionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (var resource in this.Resources.Values)
            {
                // cleanup translations
                foreach (DataRow dr in resource.StringsTable.Rows)
                {
                    if (FilterByKey(dr["Key"].ToString()) || FilterByValue(dr["NoLanguageValue"].ToString()))
                    {
                        foreach (var lang in resource.Languages)
                        {
                            if (dr[lang.Key].ToString() != dr["NoLanguageValue"].ToString())
                            {
                                dr[lang.Key] = dr["NoLanguageValue"].ToString();
                            }
                        }
                    }
                }
            }
        }

        private void importTranslatedRowsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var fileDialog = new OpenFileDialog() {Filter = "Comma separated values (*.csv)|*.csv" };
            if (Directory.Exists(_exportPath))
            {
                fileDialog.InitialDirectory = _exportPath;
            }
            var result = fileDialog.ShowDialog();
            if (result == DialogResult.OK)
            {
                try
                {
                    // read all translations
                    var translations = new List<ResourceRow>();
                    using (var ts = new FileStream(fileDialog.FileName, FileMode.Open, FileAccess.Read))
                    {
                        using (var sr = new StreamReader(ts, Encoding.GetEncoding(1252)))
                        {
                            var headers = ResourceRow.GetHeaderNames();
                            while (!sr.EndOfStream)
                            {
                                var line = sr.ReadLine();
                                if (!String.IsNullOrEmpty(line) && line != headers)
                                {
                                    var res = ResourceRow.Parse(line);
                                    translations.Add(res);
                                }
                            }
                        }
                    }

                    // loop over all resources and look if there is a translation in memory
                    foreach (var resource in this.Resources.Values)
                    {
                        var res = resource;
                        // export
                        foreach (DataRow dr in res.StringsTable.Rows)
                        {
                            var key = dr["Key"].ToString();
                            var resourceRows = translations.Where(t => t.Key == key && t.ResourcefileName == res.Filename);
                            var resourceRow = resourceRows.FirstOrDefault();
                            if (resourceRow != null)
                            {
                                foreach (var lang in resourceRow.Translations)
                                {
                                    dr[lang.Culture] = lang.RemoveLanguageSuffixIfExists().Value;
                                }
                            }
                        }
                    }
                    _exportPath = fileDialog.InitialDirectory;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(String.Format("Error: {0}", ex.GetBaseException().Message));
                }
            }
        }

        //=========================================================================================

        //
    }

    public class LanguageHolder
    {
        public string Filename { get; set; }
        public string Id { get; set; }

        public override string ToString()
        {
            return this.Id;
        }
    }

    public class PathHolder
    {
        public string Id { get; set; }
    }
}