
using static Rigol.Rigol;

Rigol.Rigol rigol = new("192.168.0.150");

rigol.Connect();
//rigol.Reset();

// rigol.AutoScale();
rigol.Clear();
// rigol.Single();

rigol.IDN();

rigol.Run();


Waveform waveform = new(rigol);
Measure measure = new(rigol);

measure.GetItem(Measure.Item.Vmax, out double vmax);
measure.GetItem(Measure.Item.Period, out double period);
measure.GetItem(Measure.Item.Vpp, out double vpp);
measure.GetItem(Measure.Item.Pduty, out double pduty);
measure.GetItem(Measure.Item.Vrms, out double vrms);
measure.GetItem(Measure.Item.Frequency, out double frequency);




rigol.Stop();

waveform.SetSource();
waveform.SetMode();
waveform.SetFormat();
waveform.Preamble();
waveform.GetData();


rigol.Disconnect();


