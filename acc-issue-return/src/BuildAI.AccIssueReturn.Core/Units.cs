using System;
using System.Text;

namespace BuildAI.AccIssueReturn.Core;

public enum LengthUnit { Metre, Millimetre, Centimetre, Foot, Inch }
public enum UnitParseStatus { Known, Unknown, Missing }

public sealed class UnitParseResult
{
    public UnitParseStatus Status { get; set; }
    public LengthUnit? Unit { get; set; }
    public double? MetresPerUnit { get; set; }
    public string OriginalValue { get; set; } = "";
    public string NormalizedValue { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsKnown => Status == UnitParseStatus.Known && Unit.HasValue && MetresPerUnit.HasValue;
}

public static class LengthUnits
{
    public const double FeetPerMeter = 3.280839895013123;
    public const double MillimetersPerFoot = 304.8;

    public static UnitParseResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new UnitParseResult { Status=UnitParseStatus.Missing, OriginalValue=value??"", Reason="Unit is missing." };
        var normalized=Normalize(value!);
        LengthUnit? unit=null; double? metres=null;
        switch(normalized)
        {
            case "m": case "meter": case "meters": case "metre": case "metres": unit=LengthUnit.Metre;metres=1d;break;
            case "mm": case "millimeter": case "millimeters": case "millimetre": case "millimetres": unit=LengthUnit.Millimetre;metres=.001d;break;
            case "cm": case "centimeter": case "centimeters": case "centimetre": case "centimetres": unit=LengthUnit.Centimetre;metres=.01d;break;
            case "ft": case "foot": case "feet": case "decimalfoot": case "decimalfeet": unit=LengthUnit.Foot;metres=.3048d;break;
            case "in": case "inch": case "inches": unit=LengthUnit.Inch;metres=.0254d;break;
        }
        return unit.HasValue
            ? new UnitParseResult { Status=UnitParseStatus.Known,Unit=unit,MetresPerUnit=metres,OriginalValue=value!,NormalizedValue=normalized,Reason="Known length unit." }
            : new UnitParseResult { Status=UnitParseStatus.Unknown,OriginalValue=value!,NormalizedValue=normalized,Reason="Unknown length unit: "+value };
    }

    private static string Normalize(string value)
    {
        var b=new StringBuilder();
        foreach(var c in value.Trim().ToLowerInvariant())if(!char.IsWhiteSpace(c)&&c!='-'&&c!='_')b.Append(c);
        return b.ToString();
    }
}
