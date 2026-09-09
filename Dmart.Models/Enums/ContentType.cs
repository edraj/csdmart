using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

[JsonConverter(typeof(ContentTypeJsonConverter))]
// Mirrors dmart/backend/models/enums.py::ContentType, with one addition.
//
// `image` is BOTH a member here and split per-format. Python's class defines only
// the per-format values and resolves a bare "image" through _missing_ → image_jpeg;
// carrying the member explicitly means a stored "image" round-trips instead of
// failing to parse. PayloadHandler.MimeFor treats it the same way Python's
// _missing_ does when the filename says nothing more specific.
//
// `binary` has no Python counterpart. It exists because InferContentType has to
// name "an upload I could not identify", and the alternative it used to pick —
// json — is a claim about the bytes that is simply false for a .docx or a .zip:
// it made the payload endpoint serve them as application/json, and the MCP
// download tool label them the same way. A value that means "opaque" is the
// honest answer, and an unknown token is a far smaller parity problem than a
// wrong known one. Anything reading these rows should treat it as
// application/octet-stream.
public enum ContentType
{
    [EnumMember(Value = "text")]        Text,
    [EnumMember(Value = "comment")]     Comment,
    [EnumMember(Value = "reaction")]    Reaction,
    [EnumMember(Value = "markdown")]    Markdown,
    [EnumMember(Value = "html")]        Html,
    [EnumMember(Value = "json")]        Json,
    [EnumMember(Value = "image")]       Image,
    [EnumMember(Value = "image_jpeg")]  ImageJpeg,
    [EnumMember(Value = "image_png")]   ImagePng,
    [EnumMember(Value = "image_svg")]   ImageSvg,
    [EnumMember(Value = "image_gif")]   ImageGif,
    [EnumMember(Value = "image_webp")]  ImageWebp,
    [EnumMember(Value = "python")]      Python,
    [EnumMember(Value = "pdf")]         Pdf,
    [EnumMember(Value = "audio")]       Audio,
    [EnumMember(Value = "video")]       Video,
    [EnumMember(Value = "csv")]         Csv,
    [EnumMember(Value = "parquet")]     Parquet,
    [EnumMember(Value = "jsonl")]       Jsonl,
    [EnumMember(Value = "apk")]         Apk,
    [EnumMember(Value = "sqlite")]      Sqlite,
    [EnumMember(Value = "binary")]      Binary,
}
