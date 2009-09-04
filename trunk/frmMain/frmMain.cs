using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using wyDay.Controls;
using wyUpdate.Common;
using wyUpdate.Downloader;

namespace wyUpdate
{
    public partial class frmMain : Form
    {
        #region Private variables

        public bool IsAdmin;

        public readonly UpdateEngine update = new UpdateEngine();
        VersionChoice updateFrom;

        UpdateDetails updtDetails;

        FileDownloader downloader;
        InstallUpdate installUpdate;

        readonly ClientLanguage clientLang = new ClientLanguage();

        Frame frameOn;
        bool isCancelled;

        string error;
        string errorDetails;

        // full filename of the update & servers files 
        string updateFilename;
        string serverFileLoc;

        //client file location
        string clientFileLoc;

        // are we using the -server commandline switch?
        string serverOverwrite;

        // base directory: same path as the executable, unless specified
        string baseDirectory;
        //the extract directory
        string tempDirectory;

        readonly PanelDisplay panelDisplaying = new PanelDisplay(500, 320);

        // the first step wyUpdate should take
        UpdateStepOn startStep = UpdateStepOn.Nothing;

        //does the client need elevation?
        bool needElevation;
        bool willSelfUpdate;

        //--Uninstalling
        bool uninstalling;

        //--Silent updating/uninstalling
        bool isSilent;
        public int ReturnCode { get; set; }


        //used after a self update or elevation
        bool continuingUpdate; 

        //Pre-RC2 compatability:
        ClientFileType clientFileType;

        bool selfUpdateFromRC1;
        string newClientLocation; //self update from RC1

        // handle hidden form
        bool _isApplicationRun = true;
        bool StartFormHidden;
        
        // start hidden, close if no update, show if update
        bool QuickCheck;
        bool QuickCheckNoErr;

        #endregion Private variables

        public frmMain(string[] args)
        {
            //sets to SegoeUI on Vista
            Font = SystemFonts.MessageBoxFont;

            // check if user is an admin for windows 2000+
            IsAdmin = VistaTools.IsUserAnAdmin();

            InitializeComponent();

            //enable Lazy SSL for all downloads
            FileDownloader.EnableLazySSL();

            //resize the client so its client region = 500x360
            if (ClientRectangle.Width != 500)
                Width = (Width - ClientRectangle.Width) + 500;

            if (ClientRectangle.Height != 360)
                Height = (Height - ClientRectangle.Height) + 360;

            //add the panelDisplaying to form
            panelDisplaying.TabIndex = 0;
            Controls.Add(panelDisplaying);

            //process commandline argument
            Arguments commands = new Arguments(args);
            ProcessArguments(commands);

            try
            {
                // load the self update information
                if (!string.IsNullOrEmpty(selfUpdateFileLoc))
                {
                    LoadSelfUpdateData(selfUpdateFileLoc);

                    //TODO: wyUp 3.0: excise this hack
                    //if the loaded file is from RC1, then update self and bail out
                    if (selfUpdateFromRC1)
                    {
                        //install the new client, and relaunch it to continue the update
                        if (needElevation && NeedElevationToUpdate())
                        {
                            //the user "elevated" as a non-admin user
                            //warn the user of their idiocy
                            error = clientLang.AdminError;

                            //set to false so new client won't be launched in frmMain_Load()
                            selfUpdateFromRC1 = false;

                            ShowFrame(Frame.Error);
                        }
                        else
                        {
                            needElevation = false;

                            //Install the new client
                            File.Copy(newClientLocation, oldClientLocation, true);

                            //Relaunch self in OnLoad()
                        }

                        //bail out
                        return;
                    }
                }

                //Load the client information
                if (clientFileType == ClientFileType.PreRC2)
                    //TODO: wyUp 3.0: stop supporting old client files (barely anyone uses RC2).
                    update.OpenObsoleteClientFile(clientFileLoc);
                else
                    update.OpenClientFile(clientFileLoc, clientLang);

                clientLang.SetVariables(update.ProductName, update.InstalledVersion);
            }
            catch (Exception ex)
            {
                clientLang.SetVariables(update.ProductName, update.InstalledVersion);

                error = "Client file failed to load. The client.wyc file might be corrupt.";
                errorDetails = ex.Message;

                ShowFrame(Frame.Error);
                return;
            }

            //sets up Next & Cancel buttons
            SetButtonText();

            //set header alignment, etc.
            panelDisplaying.HeaderImageAlign = update.HeaderImageAlign;

            if (update.HeaderTextIndent >= 0)
                panelDisplaying.HeaderIndent = update.HeaderTextIndent;

            panelDisplaying.HideHeaderDivider = update.HideHeaderDivider;

            try
            {
                if (!string.IsNullOrEmpty(update.HeaderTextColorName))
                    panelDisplaying.HeaderTextColor = Color.FromName(update.HeaderTextColorName);
            }
            catch { }

            //load the Side/Top images
            panelDisplaying.TopImage = update.TopImage;
            panelDisplaying.SideImage = update.SideImage;

            if (isAutoUpdateMode)
            {
                //TODO: create the temp folder where we'll store the updates long term

                tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), update.ProductName);

                try
                {
                    Directory.CreateDirectory(tempDirectory);

                    // load the previous auto update state from "autoupdate"
                    LoadAutoUpdateData();
                }
                catch (Exception ex)
                {
                    error = "Failed to load the AutoUpdate State file.";
                    errorDetails = ex.Message;

                    ShowFrame(Frame.Error);
                }
            }
            else if (SelfUpdating)
            {
                try
                {
                    //load the self-update server file
                    LoadClientServerFile(null);
                    clientLang.NewVersion = update.NewVersion;
                }
                catch (Exception ex)
                {
                    error = clientLang.ServerError;
                    errorDetails = ex.Message;

                    ShowFrame(Frame.Error);
                    return;
                }

                if (needElevation && NeedElevationToUpdate())
                {
                    //the user "elevated" as a non-admin user
                    //warn the user of their idiocy
                    error = clientLang.AdminError;

                    ShowFrame(Frame.Error);
                }
                else
                {
                    needElevation = false;

                    //begin updating the product
                    DownloadUpdate();
                }
            }
            else if (continuingUpdate) //continuing from elevation or self update (or both)
            {
                try
                {
                    //load the server file (without filling the 'changes' box & without downloading the wyUpdate Server file)
                    LoadServerFile(false);
                }
                catch (Exception ex)
                {
                    error = clientLang.ServerError;
                    errorDetails = ex.Message;

                    ShowFrame(Frame.Error);
                    return;
                }

                if (needElevation && NeedElevationToUpdate())
                {
                    // the user "elevated" as a non-admin user
                    // warn the user of their idiocy
                    error = clientLang.AdminError;

                    ShowFrame(Frame.Error);
                }
                else
                {
                    needElevation = false;

                    //begin updating the product
                    DownloadUpdate();
                }
            }
            else if (!uninstalling)
                startStep = UpdateStepOn.Checking;
        }

        protected override void SetVisibleCore(bool value)
        {
            if (_isApplicationRun)
            {
                _isApplicationRun = false;

                base.SetVisibleCore(StartFormHidden ? false : value);


                // run the OnLoad code

                if (uninstalling)
                {
                    ShowFrame(Frame.Uninstall);
                }
                else if (selfUpdateFromRC1)
                {
                    //if the loaded file is from RC1, then update self and bail out

                    //Relaunch self
                    StartSelfElevated();
                }
                else if (startStep != UpdateStepOn.Nothing)
                {
                    PrepareStepOn(startStep);
                }

                //TODO: load other steps from the autoupdate file

                return;
            }

            base.SetVisibleCore(value);
        }


        private void ProcessArguments(Arguments commands)
        {
            if (commands["supdf"] != null)
            {
                //the client is in self update mode
                selfUpdateFileLoc = commands["supdf"];
            }
            else
            {
                // wait mode - for automatic updates
                if (commands["autoupdate"] != null)
                {
                    SetupAutoupdateMode();
                }

                if (commands["quickcheck"] != null)
                {
                    StartFormHidden = true;
                    QuickCheck = true;

                    if (commands["noerr"] != null)
                        QuickCheckNoErr = true;
                }

                //client data file
                if (commands["cdata"] != null)
                {
                    clientFileLoc = commands["cdata"];

                    if (clientFileLoc.EndsWith("iuc", StringComparison.InvariantCultureIgnoreCase))
                        clientFileType = ClientFileType.PreRC2;
                    else if (clientFileLoc.EndsWith("iucz", StringComparison.InvariantCultureIgnoreCase))
                        clientFileType = ClientFileType.RC2;
                    else
                        clientFileType = ClientFileType.Final;
                }
                else
                {
                    clientFileLoc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.wyc");
                    clientFileType = ClientFileType.Final;

                    //try the RC-2 filename
                    if (!File.Exists(clientFileLoc))
                    {
                        clientFileLoc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "iuclient.iucz");
                        clientFileType = ClientFileType.RC2;
                    }
                    
                    //try Pre-RC2 filename
                    if (!File.Exists(clientFileLoc))
                    {
                        //if it doesn't exist, try without the 'z'
                        clientFileLoc = clientFileLoc.Substring(0, clientFileLoc.Length - 1);
                        clientFileType = ClientFileType.PreRC2;
                    }
                }

                //set basedirectory as the location of the executable
                baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

                if (commands["basedir"] != null && Directory.Exists(commands["basedir"]))
                {
                    //if the specified directory exists, then set as directory
                    baseDirectory = commands["basedir"];
                }

                if (commands["tempdir"] != null && Directory.Exists(commands["tempdir"]))
                {
                    //set the temp directory
                    tempDirectory = commands["tempdir"];
                }
                else if (!isAutoUpdateMode) //if the tempDir hasn't been created (and not isAutoUpdateMode)
                {
                    //create my own "random" temp dir.
                    tempDirectory = Path.Combine(Path.GetTempPath(), @"wyup" + DateTime.Now.ToString("ddMMssfff"));
                    Directory.CreateDirectory(tempDirectory);
                }

                //uninstall any newly created folders, files, or registry
                if (commands["uninstall"] != null)
                    uninstalling = true;


                // load the passed server argument
                if (commands["server"] != null)
                    serverOverwrite = commands["server"];


                //only allow silent uninstalls 
                //TODO: allow silent checking and updating
                if (uninstalling && commands["s"] != null)
                {
                    isSilent = true;

                    WindowState = FormWindowState.Minimized;
                    ShowInTaskbar = false;
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            //only warn if after the welcome page
            //and not self updating/elevating
            if (needElevation || willSelfUpdate || SelfUpdating || isSilent || isAutoUpdateMode ||
                isCancelled || panelDisplaying.TypeofFrame == FrameType.WelcomeFinish)
            {
                //close the form
                e.Cancel = false;
            }
            else //currently updating
            {
                //stop closing
                e.Cancel = true;

                //prompt the user if they really want to cancel
                CancelUpdate();
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            //if not self updating, then delete temp files.
            if (!(needElevation || willSelfUpdate || SelfUpdating || isAutoUpdateMode))
            {
                //if the temp directory exists, remove it
                if (Directory.Exists(tempDirectory))
                {
                    try
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                    catch { }
                }
            }

            base.OnClosed(e);
        }
    }
}