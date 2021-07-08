using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace HATarmBike
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        /// <summary>
        /// parameter for serial Port
        /// </summary>
        private SerialDevice serialPort = null;
        DataReader dataReaderObject = null;

        private ObservableCollection<DeviceInformation> listOfDevices;
        private CancellationTokenSource ReadCancellationTokenSource;

        /// <summary>
        /// parameter for BLE
        /// </summary>
        public string SelectedBleDeviceId;
        public string SelectedBleDeviceName = "No device selected";

        private ObservableCollection<BluetoothLEDeviceDisplay> KnownDevices = new ObservableCollection<BluetoothLEDeviceDisplay>();
        private List<DeviceInformation> UnknownDevices = new List<DeviceInformation>();

        private DeviceWatcher deviceWatcher;

        private BluetoothLEDevice bluetoothLeDevice = null;
        private GattCharacteristic selectedCharacteristic;

        // Only one registered characteristic at a time.
        private GattCharacteristic registeredCharacteristic;
        private GattPresentationFormat presentationFormat;

        //heartRete value
        private GattCharacteristic HartRateCharacteristic;
        GattCharacteristic HRCTag;
      
        //battery level 
        private GattCharacteristic BatteryCharacteristic;
        GattCharacteristic BLCTag;

        //SpO2 value 
        private GattCharacteristic spO2Characteristic;
        GattCharacteristic SPO2CTag;

        private bool subscribedForNotifications = false;

        public MainPage()
        {
            this.InitializeComponent();
            listOfDevices = new ObservableCollection<DeviceInformation>();
            ListAvailablePorts();
            StartBleDeviceWatcher();
        }

        #region Error Codes
        readonly int E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED = unchecked((int)0x80650003);
        readonly int E_BLUETOOTH_ATT_INVALID_PDU = unchecked((int)0x80650004);
        readonly int E_ACCESSDENIED = unchecked((int)0x80070005);
        readonly int E_DEVICE_NOT_AVAILABLE = unchecked((int)0x800710df); // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE)
        #endregion

        #region list available serial ports
        private async void ListAvailablePorts()
        {
            try
            {
                string aqs = SerialDevice.GetDeviceSelector();
                var dis = await DeviceInformation.FindAllAsync(aqs);

                status.Text = "Select the device and connect";

                for (int i = 0; i < dis.Count; i++)
                {
                    listOfDevices.Add(dis[i]);
                }

                DeviceListSource.Source = listOfDevices;
                comPortInput.IsEnabled = true;
                ConnectDevices.SelectedIndex = -1;
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }

        #endregion

        #region list available BLE

        private void StartBleDeviceWatcher()
        {
            // Additional properties we would like about the device.
            // Property strings are documented here https://msdn.microsoft.com/en-us/library/windows/desktop/ff521659(v=vs.85).aspx
            string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.Aep.Bluetooth.Le.IsConnectable" };

            // BT_Code: Example showing paired and non-paired in a single query.
            string aqsAllBluetoothLEDevices = "(System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";

            deviceWatcher =
                    DeviceInformation.CreateWatcher(
                        aqsAllBluetoothLEDevices,
                        requestedProperties,
                        DeviceInformationKind.AssociationEndpoint);

            // Register event handlers before starting the watcher.
            deviceWatcher.Added += DeviceWatcher_Added;
            deviceWatcher.Updated += DeviceWatcher_Updated;
            deviceWatcher.Removed += DeviceWatcher_Removed;

            // Start over with an empty collection.
            KnownDevices.Clear();

            deviceWatcher.Start();

            BLEstatus.Text = "select device to connect";
        }

        #region device add, update, and remove

        private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation deviceInfo)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                   
                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        // Make sure device isn't already present in the list.
                        if (FindBluetoothLEDeviceDisplay(deviceInfo.Id) == null)
                        {
                            if (deviceInfo.Name != string.Empty && deviceInfo.Name.Contains("Nonin3150"))
                            {
                                // If device has a friendly name display it immediately.
                                KnownDevices.Add(new BluetoothLEDeviceDisplay(deviceInfo));
                            }
                            else
                            {
                                // Add it to a list in case the name gets updated later. 
                                UnknownDevices.Add(deviceInfo);
                            }
                        }

                    }
                }
            });
        }

        private BluetoothLEDeviceDisplay FindBluetoothLEDeviceDisplay(string id)
        {
            foreach (BluetoothLEDeviceDisplay bleDeviceDisplay in KnownDevices)
            {
                if (bleDeviceDisplay.Id == id)
                {
                    return bleDeviceDisplay;
                }
            }
            return null;
        }


        private async void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {
                   

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        BluetoothLEDeviceDisplay bleDeviceDisplay = FindBluetoothLEDeviceDisplay(deviceInfoUpdate.Id);
                        if (bleDeviceDisplay != null)
                        {
                            // Device is already being displayed - update UX.
                            bleDeviceDisplay.Update(deviceInfoUpdate);
                            return;
                        }

                        DeviceInformation deviceInfo = FindUnknownDevices(deviceInfoUpdate.Id);
                        if (deviceInfo != null )
                        {
                            deviceInfo.Update(deviceInfoUpdate);
                            // If device has been updated with a friendly name it's no longer unknown.
                            if (deviceInfo.Name != String.Empty && deviceInfo.Name.Contains("Nonin3150"))
                            {
                                KnownDevices.Add(new BluetoothLEDeviceDisplay(deviceInfo));
                                UnknownDevices.Remove(deviceInfo);
                            }
                        }
                    }
                }
            });
        }

        private DeviceInformation FindUnknownDevices(string id)
        {
            foreach (DeviceInformation bleDeviceInfo in UnknownDevices)
            {
                if (bleDeviceInfo.Id == id)
                {
                    return bleDeviceInfo;
                }
            }
            return null;
        }


        private async void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            // We must update the collection on the UI thread because the collection is databound to a UI element.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                lock (this)
                {

                    // Protect against race condition if the task runs after the app stopped the deviceWatcher.
                    if (sender == deviceWatcher)
                    {
                        // Find the corresponding DeviceInformation in the collection and remove it.
                        BluetoothLEDeviceDisplay bleDeviceDisplay = FindBluetoothLEDeviceDisplay(deviceInfoUpdate.Id);
                        if (bleDeviceDisplay != null)
                        {
                            KnownDevices.Remove(bleDeviceDisplay);
                        }

                        DeviceInformation deviceInfo = FindUnknownDevices(deviceInfoUpdate.Id);
                        if (deviceInfo != null)
                        {
                            UnknownDevices.Remove(deviceInfo);
                        }
                    }
                }
            });
        }

        #endregion

        #region Pairing

        private bool isBusy = false;

        private async void PairButton_Click()
        {
            // Do not allow a new Pair operation to start if an existing one is in progress.
            if (isBusy)
            {

                return;
            }

            isBusy = true;

            // Capture the current selected item in case the user changes it while we are pairing.
            var bleDeviceDisplay = ResultsListView.SelectedItem as BluetoothLEDeviceDisplay;

            // BT_Code: Pair the currently selected device.
            DevicePairingResult result = await bleDeviceDisplay.DeviceInformation.Pairing.PairAsync();
            
            isBusy = false;
        }

        #endregion

        #endregion

        #region BLE device connect button click
        private async void ConnectButton_Click()
        {
            var bleDeviceDisplay = ResultsListView.SelectedItem as BluetoothLEDeviceDisplay;
            if (bleDeviceDisplay != null)
            {
                BLEstatus.Text = bleDeviceDisplay.Name;
                SelectedBleDeviceId = bleDeviceDisplay.Id;
                SelectedBleDeviceName = bleDeviceDisplay.Name;
            }


            ConnectButton.IsEnabled = false;

            if (!await ClearBluetoothLEDeviceAsync())
            {
                BLEstatus.Text = "Error: Unable to reset state, try again.";
                ConnectButton.IsEnabled = true;
                return;
            }

            try
            {
                // BT_Code: BluetoothLEDevice.FromIdAsync must be called from a UI thread because it may prompt for consent.
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(SelectedBleDeviceId);

                if (bluetoothLeDevice == null)
                {
                    BLEstatus.Text= SelectedBleDeviceName;
                }
            }
            catch (Exception ex) when (ex.HResult == E_DEVICE_NOT_AVAILABLE)
            {
                BLEstatus.Text="Bluetooth radio is not on.";
            }

            if (bluetoothLeDevice != null)
            {
                // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
                // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
                // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
                GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    var services = result.Services;

                    Guid HeartRateGuid = new Guid("0000180D-0000-1000-8000-00805F9B34FB");
                    Guid batterylevelGuid = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
                    Guid ContinuuousO2guid = new Guid("46A970E0-0D5F-11E2-8B5E-0002A5D5C51B");

                    foreach (var service in services)
                    {
                        //ServiceList.Items.Add(new ComboBoxItem { Content = DisplayHelpers.GetServiceName(service), Tag = service });
                        if (service.Uuid.Equals(HeartRateGuid))
                        {
                            getHeartRateServiceCharacter(service);
                        }

                        //if (service.Uuid.Equals(batterylevelGuid))
                        //{
                        //    getBatteryLevelServiceCharacter(service);
                        //}

                        if (service.Uuid.Equals(ContinuuousO2guid))
                        {
                            getSPO2ServiceCharacter(service);
                        }
                    }
                    ConnectButton.Visibility = Visibility.Collapsed;
                    //ServiceList.Visibility = Visibility.Visible;
                }
                else
                {
                    //rootPage.NotifyUser("Device unreachable", NotifyType.ErrorMessage);
                }
            }
            ConnectButton.IsEnabled = true;
        }


        #region SpO2 value get

        private async void getSPO2ServiceCharacter(GattDeviceService SPO2serviceTag)
        {
            var service = (GattDeviceService)SPO2serviceTag;
            // a list to stortage all the characteristic of spO2 service 
            IReadOnlyList<GattCharacteristic> spO2Characterist = null;
            //get all the spO2 characteristic 
            var spO2result = service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

            try
            {
                var accessStatus = await service.RequestAccessAsync();
                if (accessStatus == DeviceAccessStatus.Allowed)
                {
                    var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        spO2Characterist = result.Characteristics;
                    }
                    else
                    {
                        spO2Characterist = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    spO2Characterist = new List<GattCharacteristic>();
                }
            }
            catch (Exception ex)
            {
                spO2Characterist = new List<GattCharacteristic>();
            }

            Guid Oimetrychracteristic = new Guid("0AAD7EA0-0D60-11E2-8E3C-0002A5D5C51B");
           
            
            foreach (GattCharacteristic c in spO2Characterist)
            {

                if (c.Uuid.Equals(Oimetrychracteristic))
                {
                    SPO2CTag = c;
                };

            }
            getSpO2ValueDisplay();
           
        }

        #region SpO2 from continous Oximetry 

        private async void getSpO2ValueDisplay()
        {
            GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
            var cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Notify;

            status = await SPO2CTag.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

            if (status == GattCommunicationStatus.Success)
            {
                spO2Characteristic = SPO2CTag;
                spO2Characteristic.ValueChanged += spO2Characteristic_ValueChanged;
            }
        }

        private async void spO2Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var SPO2formatvalue = SPO2FormatValue(args.CharacteristicValue, presentationFormat);
            var sp02message = SPO2formatvalue;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => spO2Display.Text = sp02message);

        }

        private string PAIFormatValue(IBuffer buffer, GattPresentationFormat presentationFormat)
        {
            byte[] data;
            decimal pai = 0;
            string PAI1hex = "";
            string PAI2hex = "";


            CryptographicBuffer.CopyToByteArray(buffer, out data);

            if (data != null)
            {
                var hexadecimal = BitConverter.ToString(data);
                if (hexadecimal != null)
                {
                    try
                    {
                        PAI1hex = hexadecimal.Substring(9, 2);
                        PAI2hex = hexadecimal.Substring(12, 2);
                    }
                    catch (Exception e)
                    {
                        return "0";
                    }

                    string PTI = "0x" + PAI1hex + PAI2hex;
                    var PITconvert = Convert.ToInt32(PTI, 16);
                    pai = Convert.ToDecimal(PITconvert * 0.01);

                }

            }

            return pai.ToString() + " %";
        }

        private string SPO2FormatValue(IBuffer buffer, GattPresentationFormat format)
        {

            byte[] data;
            CryptographicBuffer.CopyToByteArray(buffer, out data);
            var hexadecimal = BitConverter.ToString(data);
            string spo2hex = hexadecimal.Substring(21, 2);
            string spo2 = "0x" + spo2hex;
            var spo2convert = Convert.ToInt32(spo2, 16);

            return spo2convert.ToString();
        }

        #endregion



        #endregion

        #region battery data retrive
        private async void getBatteryLevelServiceCharacter(GattDeviceService BLservice)
        {
            var BLVservice = (GattDeviceService)BLservice;
            IReadOnlyList<GattCharacteristic> BLVcharacteristics = null;
            var BLVresult = await BLservice.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            try
            {
                var accessStatus = await BLVservice.RequestAccessAsync();
                if (accessStatus == DeviceAccessStatus.Allowed)
                {

                    var result = await BLVservice.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        BLVcharacteristics = BLVresult.Characteristics;
                    }
                    else
                    {
                        BLVcharacteristics = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    BLVcharacteristics = new List<GattCharacteristic>();
                }
            }
            catch (Exception ex)
            {
                BLVcharacteristics = new List<GattCharacteristic>();
            }

            //assight Battery Level Characteristic to BLCTag
            foreach (GattCharacteristic c in BLVcharacteristics)
            {
                if (DisplayHelpers.GetCharacteristicName(c).Equals("BatteryLevel"))
                {
                    BLCTag = c;
                };
            }
            getBatteryLevelValueDisplay();
        }

        private async void getBatteryLevelValueDisplay()
        {
            //only get value at once, need subscribe as well
            GattReadResult result = await BLCTag.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                string formattedResult = BatteryLevelValueFormatValue(result.Value, presentationFormat);
                var message = formattedResult;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => BatteryLevelData.Text = message);
            }

            //TODO: also need to subscribe
        }

        private void BatteryLevelChangedHandler()
        {
            BatteryCharacteristic = BLCTag;
            BLCTag.ValueChanged += BLVCharacteristic_ValueChangedAsync;
        }

        private async void BLVCharacteristic_ValueChangedAsync(GattCharacteristic sender, GattValueChangedEventArgs args)
        {

            var BatteryLevelValue = BatteryLevelValueFormatValue(args.CharacteristicValue, presentationFormat);
            var BLVmessage = BatteryLevelValue;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => BatteryLevelData.Text = BLVmessage);
        }

        private string BatteryLevelValueFormatValue(IBuffer buffer, GattPresentationFormat format)
        {

            byte[] data;
            CryptographicBuffer.CopyToByteArray(buffer, out data);

            if (data != null)
            {

                if (BLCTag.Uuid.Equals(GattCharacteristicUuids.BatteryLevel))
                {
                    try
                    {
                        return "Battery Level: " + data[0].ToString() + "%";
                    }
                    catch (ArgumentException)
                    {
                        return "Battery Level: (unable to parse)";
                    }
                }

            }
            else
            {
                return "Empty data received";
            }
            return "Unknown format no data recived";
        }


        #endregion

        #region Heart Rate data retrive 

        //as soon as it get connect go straight to get the heart rate service and characteristic and assight it to  
        private async void getHeartRateServiceCharacter(GattDeviceService HRServiceTag)
        {
            var HRservice = (GattDeviceService)HRServiceTag;
            //storage all the Heart Rate characteristic 
            IReadOnlyList<GattCharacteristic> HRcharacteristics = null;
            //get all the Heart Rate characteristic
            var HRresult = await HRservice.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

            //get the service and characteristic here
            try
            {
                var accessStatus = await HRservice.RequestAccessAsync();
                if (accessStatus == DeviceAccessStatus.Allowed)
                {

                    var result = await HRservice.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        HRcharacteristics = HRresult.Characteristics;
                    }
                    else
                    {
                        HRcharacteristics = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    HRcharacteristics = new List<GattCharacteristic>();
                }
            }
            catch (Exception ex)
            {
                HRcharacteristics = new List<GattCharacteristic>();
            }

            //get the one characteristic that i need
            foreach (GattCharacteristic c in HRcharacteristics)
            {
                if (DisplayHelpers.GetCharacteristicName(c).Equals("HeartRateMeasurement"))
                {
                    HRCTag = c;
                };
            }
            getHeartRateValueDisplay();
        }

        private async void getHeartRateValueDisplay()
        {
            GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
            var cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Notify;

            try
            {
                status = await HRCTag.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

                if (status == GattCommunicationStatus.Success)
                {
                    HeartRateValueChangedHandler();
                }

            }
            catch (UnauthorizedAccessException ex)
            {

            }
        }

        private void HeartRateValueChangedHandler()
        {
            HartRateCharacteristic = HRCTag;
            HRCTag.ValueChanged += HRCharacteristic_ValueChanged;
        }


        private async void HRCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {

            var HeartRatevalue = HeartRateFormatValue(args.CharacteristicValue, presentationFormat);
            var HRmessage = HeartRatevalue;
           
            //var Timemessage = $"At {DateTime.Now:hh:mm:ss}";
            //Timer = Timemessage;

            //await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => TimeNow.Text = Timemessage);

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HeartReteDataDisply.Text = HeartRatevalue);
        }

        private string HeartRateFormatValue(IBuffer buffer, GattPresentationFormat format)
        {

            byte[] data;
            CryptographicBuffer.CopyToByteArray(buffer, out data);

            if (data != null)
            {

                if (HRCTag.Uuid.Equals(GattCharacteristicUuids.HeartRateMeasurement))
                {
                    try
                    {
                        return ParseHeartRateValue(data).ToString();
                    }
                    catch (ArgumentException)
                    {
                        return "Heart Rate: (unable to parse)";
                    }
                }

            }
            else
            {
                return "Empty data received";
            }
            return "Unknown format no data recived";
        }

        private static ushort ParseHeartRateValue(byte[] data)
        {
            // Heart Rate profile defined flag values
            const byte heartRateValueFormat = 0x01;

            byte flags = data[0];
            bool isHeartRateValueSizeLong = ((flags & heartRateValueFormat) != 0);

            if (isHeartRateValueSizeLong)
            {
                return BitConverter.ToUInt16(data, 1);
            }
            else
            {
                return data[1];
            }
        }



        #endregion 



        private async Task<bool> ClearBluetoothLEDeviceAsync()
        {
            if (subscribedForNotifications)
            {
                // Need to clear the CCCD from the remote device so we stop receiving notifications
                var result = await registeredCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                if (result != GattCommunicationStatus.Success)
                {
                    return false;
                }
                else
                {
                    selectedCharacteristic.ValueChanged -= Characteristic_ValueChanged;
                    subscribedForNotifications = false;
                }
            }
            bluetoothLeDevice?.Dispose();
            bluetoothLeDevice = null;
            return true;
        }

        private async void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            // BT_Code: An Indicate or Notify reported that the value has changed.
            // Display the new value with a timestamp.
            //var newValue = FormatValueByPresentation(args.CharacteristicValue, presentationFormat);
            //var message = $"Value at {DateTime.Now:hh:mm:ss.FFF}: {newValue}";
            //await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
            //    () => CharacteristicLatestValue.Text = message);
        }

        #endregion


        private async void startConnect_Click(object sender, RoutedEventArgs e)
        {
            var selection = ConnectDevices.SelectedItems;

            if (selection.Count <= 0)
            {
                status.Text = "Select a device and connect";
                return;
            }

            DeviceInformation entry = (DeviceInformation)selection[0];

            try
            {
                serialPort = await SerialDevice.FromIdAsync(entry.Id);

                if (serialPort == null) return;

                // Disable the 'Connect' button 
                comPortInput.IsEnabled = false;

                // Configure serial settings
                serialPort.WriteTimeout = TimeSpan.FromMilliseconds(1000);
                serialPort.ReadTimeout = TimeSpan.FromMilliseconds(1000);
                serialPort.BaudRate = 14400;
                //serialPort.BaudRate = 115200;
                //serialPort.BaudRate = 9600;
                serialPort.Parity = SerialParity.None;
                serialPort.StopBits = SerialStopBitCount.One;
                serialPort.DataBits = 8;
                serialPort.Handshake = SerialHandshake.None;

                // Display configured settings
                status.Text = "Serial port configured successfully: ";
                status.Text += serialPort.BaudRate + "-";
                status.Text += serialPort.DataBits + "-";
                status.Text += serialPort.Parity.ToString() + "-";
                status.Text += serialPort.StopBits;

                // Set the RcvdText field to invoke the TextChanged callback
                // The callback launches an async Read task to wait for data
                rcvdText.Text = "Waiting for data...";

                // Create cancellation token object to close I/O operations when closing the device
                ReadCancellationTokenSource = new CancellationTokenSource();

                // Enable 'WRITE' button to allow sending data
               

                Listen();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                comPortInput.IsEnabled = true;
            }
        }

        private async void Listen()
        {
            try
            {
                if (serialPort != null)
                {
                    dataReaderObject = new DataReader(serialPort.InputStream);

                    // keep reading the serial input
                    while (true)
                    {
                        await ReadAsync(ReadCancellationTokenSource.Token);
                    }
                }
            }
            catch (TaskCanceledException tce)
            {
                status.Text = "Reading task was cancelled, closing device and cleaning up";
                CloseDevice();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                // Cleanup once complete
                if (dataReaderObject != null)
                {
                    dataReaderObject.DetachStream();
                    dataReaderObject = null;
                }
            }
        }

        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            Task<UInt32> loadAsyncTask;

            uint ReadBufferLength = 1024;

            // If task cancellation was requested, comply
            cancellationToken.ThrowIfCancellationRequested();

            // Set InputStreamOptions to complete the asynchronous read operation when one or more bytes is available
            dataReaderObject.InputStreamOptions = InputStreamOptions.Partial;

            using (var childCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // Create a task object to wait for data on the serialPort.InputStream
                loadAsyncTask = dataReaderObject.LoadAsync(ReadBufferLength).AsTask(childCancellationTokenSource.Token);

                // Launch the task and wait
                UInt32 bytesRead = await loadAsyncTask;
                if (bytesRead > 0)
                {
                    string dataReturn = dataReaderObject.ReadString(bytesRead);
                    if (dataReturn.Contains(','))
                    {
                        string[] data = dataReturn.Split(',');
                        rcvdText.Text = data[0];
                        rpmText.Text = data[1];
                    }

                    
                    status.Text = "bytes read successfully!";
                }
            }
        }

        private void CloseDevice()
        {
            if (serialPort != null)
            {
                serialPort.Dispose();
            }
            serialPort = null;

            comPortInput.IsEnabled = true;
            rcvdText.Text = "";
            listOfDevices.Clear();
        }


        private void closeDevice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                status.Text = "";
                CancelReadTask();
                CloseDevice();
                ListAvailablePorts();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }


        private void CancelReadTask()
        {
            if (ReadCancellationTokenSource != null)
            {
                if (!ReadCancellationTokenSource.IsCancellationRequested)
                {
                    ReadCancellationTokenSource.Cancel();
                }
            }
        }



        #region buttom click

        int ratingValue;
        int arrayCount = 1;

        public ObservableCollection<RatingFeeling> LstSource
        {
            get { return lstSource; }
        }

        private ObservableCollection<RatingFeeling> lstSource = new ObservableCollection<RatingFeeling>
        {
            new RatingFeeling() { Time = "1-10", Rating = 10 },
            new RatingFeeling() { Time = "1-10", Rating = 1 },
        };

        private void Maximal_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 10;
            //RatingValue.Text = "10";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 10 });
            arrayCount++;
        }

        private void Really_Really_hard_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 9;
            //RatingValue.Text = "9";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 9 });
            arrayCount++;
        }

        private void Really_hard_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 8;
            //RatingValue.Text = "8";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 8 });
            arrayCount++;
        }

        private void Challenging_hard_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 7;
            //RatingValue.Text = "7";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 7 });
            arrayCount++;
        }

        private void Hard_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 6;
            //RatingValue.Text = "6";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 6 });
            arrayCount++;
        }

        private void Challenging_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 5;
            //RatingValue.Text = "5";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 5 });
            arrayCount++;
        }

        private void Moderate_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 4;
            //RatingValue.Text = "4";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            //lstSource[0].Rating = 4;
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 4 });
            //ratingArray.Add(ratingValue);
            arrayCount++;
        }

        private void Easy_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 3;
            //RatingValue.Text = "3";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 3 });
            arrayCount++;
        }

        private void Really_Easy_Button_Click_1(object sender, RoutedEventArgs e)
        {
            ratingValue = 2;
            //RatingValue.Text = "2";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 2 });
            arrayCount++;
        }

        private void Rest_Button_Click(object sender, RoutedEventArgs e)
        {
            ratingValue = 1;
            //RatingValue.Text = "1";
            DateTime localDate = DateTime.Now;
            var now = localDate.ToString("HH:mm:ss");
            lstSource.Add(new RatingFeeling() { Time = now, Rating = 1 });
            arrayCount++;
        }

        #endregion


    }

    public class RatingFeeling : INotifyPropertyChanged
    {
        private int _rating;
        private string _time;
        public string Time
        {
            get { return _time; }
            set
            {
                this._time = value;
                NotifyPropertyChanged("time");
            }
        }
        public int Rating
        {

            get { return _rating; }
            set
            {
                this._rating = value;
                NotifyPropertyChanged("rating");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

    }

}
