using System.Globalization;
using System.Text;
using IPTE.SCPI;

namespace Rigol;

public class Rigol
{
    private const uint DefaultTimeoutMs = 5_000;
    private readonly Transport transport;
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
        transport = new Transport(ipAddress, (ushort)5555, "\n", "\n");
    }
    /// <summary>
    /// Connect to the Rigol DS1054 using TCP
    /// </summary>
    /// <param name="ipAddress"></param>
    /// <returns></returns>
    public ErrorCode Connect()
    {
        // Flush on connect: an instrument that was still sending when the previous session ended
        // will otherwise hand that response to us, and every read after it is off by a message.
        var connectResult = transport.Connect(DefaultTimeoutMs, flush: true);
        var mapped = MapTransportError(connectResult);
        if (mapped != ErrorCode.None)
            return mapped;

        return Synchronise();
    }

    /// <summary>
    /// Query the instrument until it answers with its own identification, flushing whatever it
    /// still owed a previous session in between. Without this the first real query can come back
    /// holding the tail of a waveform transfer, and every read after it is one message behind.
    /// </summary>
    private ErrorCode Synchronise(int attempts = 5)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (IDN() == ErrorCode.None)
                return ErrorCode.None;

            transport.Flush();
        }

        return ErrorCode.Error;
    }

    public ErrorCode Disconnect()
    {
        var disconnectResult = transport.Disconnect();
        return MapTransportError(disconnectResult);
    }

    private static ErrorCode MapTransportError(IPTE.SCPI.ErrorCode transportError)
    {
        return transportError == IPTE.SCPI.ErrorCode.NoError
            ? ErrorCode.None
            : ErrorCode.Error;
    }


    public ErrorCode SendCommand(string command)
    {
        var sendResult = transport.Command(command, DefaultTimeoutMs);
        if (sendResult != IPTE.SCPI.ErrorCode.NoError)
            return MapTransportError(sendResult);

        return ErrorCode.None;
    }

    public ErrorCode Query(string command, ref string response)
    {
        return Query(command, ref response, DefaultTimeoutMs);
    }

    /// <summary>
    /// Query with an explicit timeout. Bulk transfers such as :WAVeform:DATA? need
    /// considerably more than the default timeout.
    /// </summary>
    public ErrorCode Query(string command, ref string response, uint timeoutMs)
    {
        var queryResult = transport.Query(command, out var responseString, timeoutMs);
        response = responseString;
        return MapTransportError(queryResult);
    }

    public ErrorCode Query(string command, out double response)
    {
        response = 0;

        string responseString = string.Empty;
        var result = Query(command, ref responseString);
        if (result != ErrorCode.None)
            return result;

        if (!double.TryParse(responseString, NumberStyles.Float, CultureInfo.InvariantCulture, out response))
            return ErrorCode.Error;

        return ErrorCode.None;
    }

    public ErrorCode Query(string command, out int response)
    {
        response = 0;

        string responseString = string.Empty;
        var result = Query(command, ref responseString);
        if (result != ErrorCode.None)
            return result;

        return int.TryParse(responseString.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out response)
            ? ErrorCode.None
            : ErrorCode.Error;
    }

    /// <summary>
    /// Query an IEEE 488.2 definite length block response, for binary instrument data such
    /// as :WAVeform:DATA? in WORD or BYTE format.
    /// </summary>
    public ErrorCode QueryBlock(string command, out byte[] data, uint timeoutMs)
    {
        var queryResult = transport.QueryBlock(command, out data, timeoutMs);
        return MapTransportError(queryResult);
    }

    public int Query(string command, ref byte[] buffer)
    {
        var sendResult = transport.Command(command, DefaultTimeoutMs);
        if (sendResult != IPTE.SCPI.ErrorCode.NoError)
            return -1;

        var readResult = transport.Read(out var responseString, DefaultTimeoutMs);
        if (readResult != IPTE.SCPI.ErrorCode.NoError)
            return -1;

        byte[] receivedBytes = Encoding.ASCII.GetBytes(responseString);
        int received = Math.Min(buffer.Length, receivedBytes.Length);
        Array.Copy(receivedBytes, 0, buffer, 0, received);

        return received;
    }
    /// <summary>
    /// Enable the waveform auto setting function. The oscilloscope will automatically adjust the
    /// vertical scale, horizontal timebase, and trigger mode according to the input signal to
    /// realize optimum waveform display. This command is equivalent to pressing the AUTO key
    /// on the front panel.
    /// </summary>
    /// <returns></returns>
    public ErrorCode AutoSet() => SendCommand(":AUToset");
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
    public ErrorCode Single()
    {
        return SendCommand(":SINGle");
    }
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
        var queryResult = Query("*IDN?", ref response);
        if (queryResult != ErrorCode.None)
            return queryResult;

        // A timed out or truncated reply used to index straight past the end of the array, and a
        // stale binary response happens to contain commas, so validate before believing it.
        string[] idn = response.Split(',');
        if (idn.Length < 4 || !IsPrintable(response) || response.Length > 200)
            return ErrorCode.Error;

        Model = idn[1];
        SerialNumber = idn[2];
        SoftwareVersion = idn[3];

        return ErrorCode.None;
    }

    private static bool IsPrintable(string text)
    {
        foreach (char c in text)
        {
            if (c < ' ' || c > '~')
                return false;
        }

        return text.Length > 0;
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
    /// <summary>
    /// Query the trigger status: TD, WAIT, RUN, AUTO or STOP.
    /// </summary>
    public ErrorCode TriggerStatus(out string status)
    {
        status = string.Empty;
        var result = Query(":TRIGger:STATus?", ref status);
        status = status.Trim();
        return result;
    }
    /// <summary>
    /// Query the vertical scale of a channel, in volts per division.
    /// </summary>
    public ErrorCode ChannelScale(int channel, out double voltsPerDiv)
        => Query($":CHANnel{channel}:SCALe?", out voltsPerDiv);
    /// <summary>
    /// Query the vertical offset of a channel, in volts. The instrument centres its graticule on
    /// minus this value, so a channel at -1.66 V offset shows +1.66 V on its centre line.
    /// </summary>
    /// <remarks>
    /// The preamble carries the same information in YOrigin, but rounded to whole ADC codes: on a
    /// DS1000Z at 1 V/div that quantises the offset to 40 mV, which is visible as a trace sitting
    /// a fraction of a division off. This query returns the offset the instrument actually holds.
    /// </remarks>
    public ErrorCode ChannelOffset(int channel, out double volts)
        => Query($":CHANnel{channel}:OFFSet?", out volts);
    /// <summary>
    /// Query the main timebase scale, in seconds per division.
    /// </summary>
    public ErrorCode TimebaseScale(out double secondsPerDiv)
        => Query(":TIMebase:MAIN:SCALe?", out secondsPerDiv);
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
        /// <summary>Time between two neighbouring points, in seconds.</summary>
        public double XIncrement { get; private set; }
        /// <summary>Start time of the waveform data, in seconds.</summary>
        public double XOrigin { get; private set; }
        public double XReference { get; private set; }
        public double YIncrement { get; private set; }
        public double YOrigin { get; private set; }
        public double YReference { get; private set; }
        /// <summary>Number of points reported by the last <see cref="Preamble"/> call.</summary>
        public int WaveformPoints { get; private set; }

        public enum Mode
        {
            Normal,
            Maximum,
            RAW
        }

        public enum Format
        {
            BYTE,
            WORD,
            ASCii
        }

        public enum Source
        {
            Channel1,
            Channel2,
            Channel3,
            Channel4,
            Math
        }

        readonly Rigol Rigol;

        public Waveform(Rigol rigol)
        {
            Rigol = rigol;
        }

        public ErrorCode SetSource(Source source = Source.Channel1)
        {
            return Rigol.SendCommand(":WAVeform:SOURce " + source);
        }

        public ErrorCode SetMode(Mode mode = Mode.Normal)
        {
            return Rigol.SendCommand(":WAVeform:MODE " + mode);
        }

        /// <summary>
        /// Transfer format currently configured on the instrument. The preamble scaling depends
        /// on it, so read the preamble after changing this.
        /// </summary>
        public Format CurrentFormat { get; private set; } = Format.BYTE;

        public ErrorCode SetFormat(Format format = Format.BYTE)
        {
            var result = Rigol.SendCommand(":WAVeform:FORMat " + format);
            if (result == ErrorCode.None)
                CurrentFormat = format;

            return result;
        }

        /// <summary>
        /// Number of points to read. Range depends on the mode: 1..1000 in NORMal,
        /// 1..memory depth in RAW.
        /// </summary>
        public ErrorCode SetPoints(int points)
        {
            return Rigol.SendCommand(":WAVeform:POINts " + points.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Read the 10 waveform parameters:
        /// format,type,points,count,xincrement,xorigin,xreference,yincrement,yorigin,yreference
        /// </summary>
        public ErrorCode Preamble()
        {
            string result = "";
            var queryResult = Rigol.Query(":WAVeform:PREamble?", ref result);
            if (queryResult != ErrorCode.None)
                return queryResult;

            string[] preamble = result.Split(',');
            if (preamble.Length < 10)
                return ErrorCode.Error;

            // A desynchronised stream can put anything here, so never throw on it.
            if (!TryParseValue(preamble[2], out double points)) return ErrorCode.Error;
            if (!TryParseValue(preamble[4], out double xIncrement)) return ErrorCode.Error;
            if (!TryParseValue(preamble[5], out double xOrigin)) return ErrorCode.Error;
            if (!TryParseValue(preamble[6], out double xReference)) return ErrorCode.Error;
            if (!TryParseValue(preamble[7], out double yIncrement)) return ErrorCode.Error;
            if (!TryParseValue(preamble[8], out double yOrigin)) return ErrorCode.Error;
            if (!TryParseValue(preamble[9], out double yReference)) return ErrorCode.Error;

            WaveformPoints = (int)points;
            XIncrement = xIncrement;
            XOrigin = xOrigin;
            XReference = xReference;
            YIncrement = yIncrement;
            YOrigin = yOrigin;
            YReference = yReference;

            return ErrorCode.None;
        }

        /// <summary>
        /// Read the waveform as voltages, using whichever transfer format is configured.
        /// WORD is the cheapest at full resolution: 2 bytes per point against roughly 14 for
        /// ASCii, at the 12-bit resolution of the instrument. BYTE halves that again but
        /// discards the lower 4 bits.
        /// </summary>
        public ErrorCode ReadSamples(out double[] samples, uint timeoutMs = 5_000)
        {
            return CurrentFormat == Format.ASCii
                ? ReadAsciiSamples(out samples, timeoutMs)
                : ReadBinarySamples(out samples, timeoutMs);
        }

        /// <summary>
        /// Read WORD or BYTE data as an IEEE 488.2 block and scale it with the preamble.
        /// </summary>
        private ErrorCode ReadBinarySamples(out double[] samples, uint timeoutMs)
        {
            samples = [];

            var queryResult = Rigol.QueryBlock(":WAVeform:DATA?", out byte[] block, timeoutMs);
            if (queryResult != ErrorCode.None)
                return queryResult;

            int bytesPerPoint = CurrentFormat == Format.WORD ? 2 : 1;
            int points = block.Length / bytesPerPoint;
            if (points == 0)
                return ErrorCode.Error;

            double[] values = new double[points];
            for (int i = 0; i < points; i++)
            {
                // WORD points are little endian.
                int raw = bytesPerPoint == 1
                    ? block[i]
                    : block[i * 2] | (block[(i * 2) + 1] << 8);

                values[i] = (raw - YOrigin - YReference) * YIncrement;
            }

            samples = values;
            return ErrorCode.None;
        }

        private ErrorCode ReadAsciiSamples(out double[] samples, uint timeoutMs)
        {
            samples = [];

            string response = string.Empty;
            var queryResult = Rigol.Query(":WAVeform:DATA?", ref response, timeoutMs);
            if (queryResult != ErrorCode.None)
                return queryResult;

            // Officially ASCii returns bare values, but strip a TMC header (#N<N digits>) if present.
            if (response.Length > 2 && response[0] == '#' && char.IsDigit(response[1]))
            {
                int digits = response[1] - '0';
                int dataStart = 2 + digits;
                if (response.Length <= dataStart)
                    return ErrorCode.Error;
                response = response[dataStart..];
            }

            string[] fields = response.Split(',', StringSplitOptions.RemoveEmptyEntries);
            double[] values = new double[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                if (!double.TryParse(fields[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return ErrorCode.Error;
            }

            samples = values;
            return values.Length > 0 ? ErrorCode.None : ErrorCode.Error;
        }

        /// <summary>
        /// Read the waveform and write it to a CSV file as time,voltage pairs.
        /// </summary>
        public ErrorCode GetData(string path = "waveform_data.csv")
        {
            var readResult = ReadSamples(out double[] samples);
            if (readResult != ErrorCode.None)
                return readResult;

            using StreamWriter writer = new(path);
            writer.WriteLine("time_s,voltage_v");
            for (int i = 0; i < samples.Length; i++)
            {
                double time = XOrigin + (i * XIncrement);
                writer.WriteLine(FormattableString.Invariant($"{time:G9},{samples[i]:G9}"));
            }

            return ErrorCode.None;
        }

        private static bool TryParseValue(string field, out double value)
        {
            return double.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// The scope's built-in pass/fail test. Note that the DHO800/DHO900 :MASK subsystem can
    /// only generate an envelope around the currently displayed waveform via <see cref="Create"/>;
    /// there is no SCPI command to load a stored *.pf mask file. Use <see cref="SoftwareMask"/>
    /// when the mask has to be restored without the reference signal being present.
    /// </summary>
    public class Mask
    {
        private readonly Rigol rigol;

        public enum Operation
        {
            RUN,
            STOP
        }

        public Mask(Rigol rigol)
        {
            this.rigol = rigol;
        }

        /// <summary>
        /// Enable or disable pass/fail mask test (:MASK:ENABle ON|OFF).
        /// </summary>
        public ErrorCode Enable(bool enable)
        {
            return rigol.SendCommand(enable ? ":MASK:ENABle ON" : ":MASK:ENABle OFF");
        }

        /// <summary>
        /// Select the channel under test (:MASK:SOURce). A disabled channel is turned on automatically.
        /// </summary>
        public ErrorCode SetSource(int channel)
        {
            return rigol.SendCommand($":MASK:SOURce CHANnel{channel}");
        }

        /// <summary>
        /// Horizontal mask tolerance in divisions. Range 0.01 to 2 div, default 0.24.
        /// </summary>
        public ErrorCode SetX(double divisions)
        {
            return rigol.SendCommand(":MASK:X " + divisions.ToString("0.####", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Vertical mask tolerance in divisions. Range 0.04 to 2 div, default 0.24.
        /// </summary>
        public ErrorCode SetY(double divisions)
        {
            return rigol.SendCommand(":MASK:Y " + divisions.ToString("0.####", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Generate the mask from the currently displayed waveform using the X/Y tolerances.
        /// Only valid while the test is enabled and <see cref="Operation.STOP"/>.
        /// </summary>
        public ErrorCode Create()
        {
            return rigol.SendCommand(":MASK:CREate");
        }

        /// <summary>
        /// Start or stop the pass/fail test (:MASK:OPERate).
        /// </summary>
        public ErrorCode Operate(Operation operation)
        {
            return rigol.SendCommand(":MASK:OPERate " + operation);
        }

        /// <summary>
        /// Reset the passed/failed/total frame counters.
        /// </summary>
        public ErrorCode Reset()
        {
            return rigol.SendCommand(":MASK:RESet");
        }

        /// <summary>
        /// Read the pass/fail statistics.
        /// </summary>
        public ErrorCode GetStatistics(out int passed, out int failed, out int total)
        {
            passed = failed = total = 0;

            var result = rigol.Query(":MASK:PASSed?", out passed);
            if (result != ErrorCode.None) return result;

            result = rigol.Query(":MASK:FAILed?", out failed);
            if (result != ErrorCode.None) return result;

            return rigol.Query(":MASK:TOTal?", out total);
        }

        /// <summary>
        /// Query pass/fail mask test status (:MASK:ENABle?).
        /// Returns true when enabled, false when disabled.
        /// </summary>
        public ErrorCode IsEnabled(out bool enabled)
        {
            enabled = false;

            string response = string.Empty;
            var queryResult = rigol.Query(":MASK:ENABle?", ref response);
            if (queryResult != ErrorCode.None)
                return queryResult;

            string normalized = response.Trim();
            if (normalized == "1" || normalized.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                return ErrorCode.None;
            }

            if (normalized == "0" || normalized.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                enabled = false;
                return ErrorCode.None;
            }

            return ErrorCode.Error;
        }
    }
}
