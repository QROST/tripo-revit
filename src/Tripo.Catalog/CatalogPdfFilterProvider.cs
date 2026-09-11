using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Filters.Jpx.OpenJpeg;

namespace Tripo.Catalog;

internal sealed class CatalogPdfFilterProvider : BaseFilterProvider
{
    public static IFilterProvider Instance { get; } = new CatalogPdfFilterProvider();

    private CatalogPdfFilterProvider() : base(CreateFilters()) { }

    private static Dictionary<string, IFilter> CreateFilters()
    {
        IFilter ascii85 = new Ascii85Filter(), asciiHex = new AsciiHexDecodeFilter(), ccitt = new CcittFaxDecodeFilter();
        IFilter dct = new DctDecodeFilter(), flate = new FlateFilter(), runLength = new RunLengthFilter(), lzw = new LzwFilter();
        return new Dictionary<string, IFilter>
        {
            ["ASCII85Decode"] = ascii85, ["A85"] = ascii85,
            ["ASCIIHexDecode"] = asciiHex, ["AHx"] = asciiHex,
            ["CCITTFaxDecode"] = ccitt, ["CCF"] = ccitt,
            ["DCTDecode"] = dct, ["DCT"] = dct,
            ["FlateDecode"] = flate, ["Fl"] = flate,
            ["RunLengthDecode"] = runLength, ["RL"] = runLength,
            ["LZWDecode"] = lzw, ["LZW"] = lzw,
            ["JBIG2Decode"] = new Jbig2DecodeFilter(), ["JPXDecode"] = new OpenJpegJpxDecodeFilter(),
        };
    }
}
