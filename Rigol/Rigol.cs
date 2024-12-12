using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualBasic;

namespace Rigol;

public class Rigol
{
    private IPAddress IPAddress { get; set; }
    public NetworkStream stream;
    public string Model { get; set; }
    public string SerialNumber { get; set; }
    public string SoftwareVersion { get; set; }

    public enum ErrorCode
    {
        None,
        Error,
        OperationNotComplete
    }
    /// <summary>
    /// Constructor for the Rigol DS1054
    /// </summary>
    public Rigol(string ipAddress)
    {
        IPAddress = IPAddress.Parse(ipAddress);
    }
    /// <summary>
    /// Connect to the Rigol DS1054 using TCP
    /// </summary>
    /// <param name="ipAddress"></param>
    /// <returns></returns>
    public ErrorCode Connect()
    {
        var ipEndPoint = new IPEndPoint(IPAddress, 5555);

        TcpClient client = new();
        client.Connect(ipEndPoint);
        stream = client.GetStream();

        return ErrorCode.None;
    }

    public ErrorCode Disconnect()
    {
        stream.Close();

        return ErrorCode.None;
    }

    public ErrorCode SendCommand(string command)
    {
        stream.Write(Encoding.UTF8.GetBytes(command + "\n"));
        OperationComplete();
        return ErrorCode.None;
    }

    public ErrorCode Query(string command, ref string response)
    {
        stream.Write(Encoding.UTF8.GetBytes(command + "\n"));
        var buffer = new byte[1_024];
        int received = stream.Read(buffer);
        response = Encoding.UTF8.GetString(buffer, 0, received).Trim('\n');

        return ErrorCode.None;
    }
        public ErrorCode Query(string command, out double response)
    {
        stream.Write(Encoding.UTF8.GetBytes(command + "\n"));
        var buffer = new byte[1_024];
        int received = stream.Read(buffer);
        string responseString = Encoding.UTF8.GetString(buffer, 0, received).Trim('\n');
        response = double.Parse(responseString);

        if (response > 100)
            Console.WriteLine("RESPONSE: " + response);

        return ErrorCode.None;
    }
    public int Query(string command, ref byte[] buffer)
    {
        stream.Write(Encoding.UTF8.GetBytes(command + "\n"));
        int test = buffer.Length;
        // buffer = new byte[1_024];
        int received = stream.Read(buffer, 0, test);

        
        return 0;
    }
    /// <summary>
    /// Enable the waveform auto setting function. The oscilloscope will automatically adjust the
    /// vertical scale, horizontal timebase, and trigger mode according to the input signal to
    /// realize optimum waveform display. This command is equivalent to pressing the AUTO key
    /// on the front panel.
    /// </summary>
    /// <returns></returns>
    public ErrorCode AutoScale() => SendCommand(":AUToscale");
    /// <summary>
    /// Clear all the waveforms on the screen. If the oscilloscope is in the RUN state, waveform
    /// will still be displayed. This command is equivalent to pressing the CLEAR key on the front
    /// panel.
    /// </summary>
    /// <returns></returns>
    public ErrorCode Clear() => SendCommand(":CLEar");
    /// <summary>
    /// The :RUN command starts the oscilloscope. This command is equivalent to pressing the
    /// RUN/STOP key on the front panel.
    /// </summary>
    /// <returns></returns>
    public ErrorCode Run() => SendCommand(":RUN");
    /// <summary>
    /// The :STOP command stops the oscilloscope. This command is equivalent to pressing the
    /// RUN/STOP key on the front panel.
    /// </summary>
    /// <returns></returns>
    public ErrorCode Stop() => SendCommand(":STOP");
    /// <summary>
    /// Set the oscilloscope to the single trigger mode. This command is equivalent to any of the
    /// following two operations: pressing the SINGLE key on the front panel and sending
    ///the :TRIGger:SWEep SINGle command.
    /// </summary>
    /// <returns></returns>
    public ErrorCode Single() => SendCommand(":SINGle");
    /// <summary>
    /// Generate a trigger signal forcefully. This command is only applicable to the normal and
    /// single trigger modes (see the :TRIGger:SWEep command) and is equivalent to pressing
    /// the FORCE key in the trigger control area on the front panel.
    /// </summary>
    /// <returns></returns>
    public ErrorCode ForceTrigger() => SendCommand(":TFORce");
    /// <summary>
    /// Query the ID string of the instrument.
    /// </summary>
    /// <returns></returns>
    public ErrorCode IDN()
    {
        string response = "";
        Query("*IDN?", ref response);
        string[] idn = response.Split(',');
        Model = idn[1];
        SerialNumber = idn[2];
        SoftwareVersion = idn[3];

        return ErrorCode.None;
    }
    public ErrorCode ClearStatus() => SendCommand(":STATus:CLEar");
    /// <summary>
    /// n Restore the instrument to the default state.
    /// </summary>
    /// <returns></returns>
    public ErrorCode Reset() => SendCommand("*RST");
    /// <summary>
    /// Wait for the operation to finish.
    /// The subsequent command can only be carried out after the current 
    /// command has been executed.
    /// </summary>
    /// <returns></returns>
    public ErrorCode Wait() => SendCommand("*WAI");
    /// <summary>
    /// Query whether the current operation is finished.
    /// </summary>
    /// <returns></returns>
    public ErrorCode OperationComplete()
    {
        string response = "";
        Query("*OPC?", ref response);
        if(response.Contains('1'))
            return ErrorCode.None;
        else
            return ErrorCode.OperationNotComplete;
    }

    public ErrorCode SetStandardEventStatusEnable(string eventCode) => SendCommand("*ESE" + eventCode);
    public ErrorCode GetStandardEventStatusEnable(string eventCode) => SendCommand("*ESE?");
    public class Measure
    {

        public enum Item
        {
            Vmax,
            Vmin,
            Vpp,
            Vtop,
            Vbase,
            Vamp,
            Vavg,
            Vrms,
            Overshoot,
            Predhoot,
            Marea,
            MParea,
            Period,
            Frequency,
            Rtime,
            Ftime,
            Pwidth,
            Nwidth,
            Pduty,
            Nduty,
            Rdelay,
            Fdelay,
            Rphase,
            Fphase,
            Tvmax,
            Tvmin,
            Pslewrate,
            Nslewrate,
            Vupper,
            Vmid,
            Vlower,
            Variance,
            Pvrms,
            Ppulses,
            Npulses,
            Pedges,
            Nedges
        }
        Rigol Rigol;

        public Measure(Rigol rigol)
        {
            Rigol = rigol;
        }

        public ErrorCode GetItem(Item item, out double measurement)
        {
            Rigol.SendCommand(":MEASure:ITEM " + item);
            Rigol.Query(":MEASure:ITEM? " + item, out measurement);

            return ErrorCode.None;
        }

        // public ErrorCode VMax(out double vMax)
        // {
        //     Rigol.SendCommand(":MEASure:ITEM VMAX");
        //     Rigol.Query(":MEASure:ITEM? VMAX", out vMax);

        //     return ErrorCode.None;
        // }

        // public ErrorCode Period(out double period)
        // {
        //     Rigol.SendCommand(":MEASure:ITEM PERiod");
        //     Rigol.Query(":MEASure:ITEM? PERiod", out period);

        //     return ErrorCode.None;
        // }
    }

    public class Waveform
    {
        private double YIncrement { get; set; }
        private int YOrigin { get; set; }
        private int YReference { get; set; }
        private double XOrigin { get; set; }
        private double XReference { get; set; }
        private int WaveformPoints { get; set; }

        public enum Mode
        {
            Normal,
            Maximum,
            RAW
        }

        public enum Source
        {
            Channel1,
            Channel2,
            Channel3,
            Channel4,
            Math
        }

        Rigol Rigol;

        public Waveform(Rigol rigol)
        {
            Rigol = rigol;
        }

        public ErrorCode SetSource(Source source = Source.Channel1)
        {
            Rigol.SendCommand(":WAVeform:SOURce " + source);

            return ErrorCode.None;
        }

        public ErrorCode SetMode(Mode mode = Mode.Normal)
        {
            Rigol.SendCommand(":WAVeform:MODE " + mode);

            return ErrorCode.None;
        }

        public ErrorCode SetFormat(string format = "BYTE")
        {
            Rigol.SendCommand(":WAVeform:FORMat " + format);

            return ErrorCode.None;
        }

        public ErrorCode Preamble()
        {
            string result = "";
            Rigol.Query(":WAVeform:PREamble?", ref result);  // 0,0,1200,1,1.000000e-05,-5.657000e-03,0,4.000000e-02,-62,127
            string[] preamble = result.Split(',');
            WaveformPoints = int.Parse(preamble[2]);
            YIncrement = double.Parse(preamble[7]);
            YOrigin = int.Parse(preamble[8]);
            YReference = int.Parse(preamble[9]);

            return ErrorCode.None;
        }

        public ErrorCode GetData()
        {
            //SendCommand(":WAVeform:STAR 1");
            //SendCommand(":WAVeform:STOP 250000");
            byte[] buffer = new byte[WaveformPoints];
            Rigol.Query(":WAVeform:DATA?", ref buffer);
            
            int headerLength = 11;
            List<byte> waveformData = new(buffer.Skip(headerLength).Take(WaveformPoints - headerLength).ToList());
            //string data = Encoding.UTF8.GetString(buffer, 0, received);

            // Save the data to a CSV file
            using (StreamWriter writer = new StreamWriter("waveform_data.csv"))
            {
                foreach (byte b in waveformData)
                {
                    double value = (b - YOrigin - YReference) * YIncrement;
                    writer.WriteLine(value.ToString() + ", ");
                }

            }

            return ErrorCode.None;
        }
    }
}
