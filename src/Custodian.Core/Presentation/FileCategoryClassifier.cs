using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public static class FileCategoryClassifier
{
    private static readonly Dictionary<string, FileCategory> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        [".mp4"] = FileCategory.Video,
        [".mkv"] = FileCategory.Video,
        [".mov"] = FileCategory.Video,
        [".avi"] = FileCategory.Video,
        [".wmv"] = FileCategory.Video,
        [".webm"] = FileCategory.Video,
        [".flv"] = FileCategory.Video,
        [".m4v"] = FileCategory.Video,
        [".mpeg"] = FileCategory.Video,
        [".mpg"] = FileCategory.Video,
        [".3gp"] = FileCategory.Video,
        [".ts"] = FileCategory.Video,

        // Image
        [".jpg"] = FileCategory.Image,
        [".jpeg"] = FileCategory.Image,
        [".png"] = FileCategory.Image,
        [".gif"] = FileCategory.Image,
        [".bmp"] = FileCategory.Image,
        [".tiff"] = FileCategory.Image,
        [".tif"] = FileCategory.Image,
        [".webp"] = FileCategory.Image,
        [".svg"] = FileCategory.Image,
        [".ico"] = FileCategory.Image,
        [".heic"] = FileCategory.Image,
        [".raw"] = FileCategory.Image,
        [".cr2"] = FileCategory.Image,
        [".nef"] = FileCategory.Image,
        [".psd"] = FileCategory.Image,
        [".ai"] = FileCategory.Image,

        // Audio
        [".mp3"] = FileCategory.Audio,
        [".wav"] = FileCategory.Audio,
        [".flac"] = FileCategory.Audio,
        [".aac"] = FileCategory.Audio,
        [".ogg"] = FileCategory.Audio,
        [".m4a"] = FileCategory.Audio,
        [".wma"] = FileCategory.Audio,
        [".opus"] = FileCategory.Audio,
        [".aiff"] = FileCategory.Audio,

        // Document
        [".pdf"] = FileCategory.Document,
        [".doc"] = FileCategory.Document,
        [".docx"] = FileCategory.Document,
        [".odt"] = FileCategory.Document,
        [".rtf"] = FileCategory.Document,
        [".txt"] = FileCategory.Document,
        [".md"] = FileCategory.Document,
        [".epub"] = FileCategory.Document,
        [".mobi"] = FileCategory.Document,
        [".tex"] = FileCategory.Document,

        // Spreadsheet
        [".xls"] = FileCategory.Spreadsheet,
        [".xlsx"] = FileCategory.Spreadsheet,
        [".xlsm"] = FileCategory.Spreadsheet,
        [".ods"] = FileCategory.Spreadsheet,
        [".csv"] = FileCategory.Spreadsheet,
        [".tsv"] = FileCategory.Spreadsheet,

        // Presentation
        [".ppt"] = FileCategory.Presentation,
        [".pptx"] = FileCategory.Presentation,
        [".odp"] = FileCategory.Presentation,
        [".key"] = FileCategory.Presentation,

        // Code
        [".cs"] = FileCategory.Code,
        [".vb"] = FileCategory.Code,
        [".fs"] = FileCategory.Code,
        [".java"] = FileCategory.Code,
        [".kt"] = FileCategory.Code,
        [".scala"] = FileCategory.Code,
        [".py"] = FileCategory.Code,
        [".rb"] = FileCategory.Code,
        [".js"] = FileCategory.Code,
        [".jsx"] = FileCategory.Code,
        [".mjs"] = FileCategory.Code,
        [".cjs"] = FileCategory.Code,
        [".tsx"] = FileCategory.Code,
        [".html"] = FileCategory.Code,
        [".htm"] = FileCategory.Code,
        [".css"] = FileCategory.Code,
        [".scss"] = FileCategory.Code,
        [".sass"] = FileCategory.Code,
        [".less"] = FileCategory.Code,
        [".go"] = FileCategory.Code,
        [".rs"] = FileCategory.Code,
        [".c"] = FileCategory.Code,
        [".h"] = FileCategory.Code,
        [".cpp"] = FileCategory.Code,
        [".hpp"] = FileCategory.Code,
        [".cc"] = FileCategory.Code,
        [".m"] = FileCategory.Code,
        [".mm"] = FileCategory.Code,
        [".swift"] = FileCategory.Code,
        [".php"] = FileCategory.Code,
        [".sh"] = FileCategory.Code,
        [".ps1"] = FileCategory.Code,
        [".psm1"] = FileCategory.Code,
        [".bat"] = FileCategory.Code,
        [".cmd"] = FileCategory.Code,
        [".lua"] = FileCategory.Code,
        [".pl"] = FileCategory.Code,
        [".r"] = FileCategory.Code,
        [".json"] = FileCategory.Code,
        [".xml"] = FileCategory.Code,
        [".yaml"] = FileCategory.Code,
        [".yml"] = FileCategory.Code,
        [".toml"] = FileCategory.Code,
        [".sql"] = FileCategory.Code,
        [".sln"] = FileCategory.Code,
        [".csproj"] = FileCategory.Code,
        [".props"] = FileCategory.Code,
        [".targets"] = FileCategory.Code,

        // Archive
        [".zip"] = FileCategory.Archive,
        [".rar"] = FileCategory.Archive,
        [".7z"] = FileCategory.Archive,
        [".tar"] = FileCategory.Archive,
        [".gz"] = FileCategory.Archive,
        [".bz2"] = FileCategory.Archive,
        [".xz"] = FileCategory.Archive,
        [".zst"] = FileCategory.Archive,
        [".cab"] = FileCategory.Archive,
        [".iso"] = FileCategory.Archive,
        [".dmg"] = FileCategory.Archive,
        [".jar"] = FileCategory.Archive,
        [".war"] = FileCategory.Archive,
        [".nupkg"] = FileCategory.Archive,

        // Executable
        [".exe"] = FileCategory.Executable,
        [".msi"] = FileCategory.Executable,
        [".dll"] = FileCategory.Executable,
        [".so"] = FileCategory.Executable,
        [".dylib"] = FileCategory.Executable,
        [".app"] = FileCategory.Executable,
        [".appx"] = FileCategory.Executable,
        [".msix"] = FileCategory.Executable,
        [".deb"] = FileCategory.Executable,
        [".rpm"] = FileCategory.Executable,

        // Database
        [".db"] = FileCategory.Database,
        [".sqlite"] = FileCategory.Database,
        [".sqlite3"] = FileCategory.Database,
        [".mdb"] = FileCategory.Database,
        [".accdb"] = FileCategory.Database,
        [".mdf"] = FileCategory.Database,
        [".ldf"] = FileCategory.Database,
        [".bak"] = FileCategory.Database,

        // Font
        [".ttf"] = FileCategory.Font,
        [".otf"] = FileCategory.Font,
        [".woff"] = FileCategory.Font,
        [".woff2"] = FileCategory.Font,
        [".eot"] = FileCategory.Font,
        [".fon"] = FileCategory.Font,

        // System
        [".sys"] = FileCategory.System,
        [".log"] = FileCategory.System,
        [".tmp"] = FileCategory.System,
        [".cache"] = FileCategory.System,
        [".lock"] = FileCategory.System,
        [".pdb"] = FileCategory.System,
        [".lnk"] = FileCategory.System
    };

    public static FileCategory Classify(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            return FileCategory.Folder;
        }

        return ClassifyExtension(entry.Extension);
    }

    public static FileCategory ClassifyExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return FileCategory.Other;
        }

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        return ExtensionMap.TryGetValue(normalized, out var category) ? category : FileCategory.Other;
    }

    public static string Glyph(FileCategory category)
    {
        // Segoe MDL2 Assets / Segoe Fluent Icons codepoints.
        return category switch
        {
            FileCategory.Folder => "\uE8B7",
            FileCategory.Video => "\uE714",
            FileCategory.Image => "\uEB9F",
            FileCategory.Audio => "\uE8D6",
            FileCategory.Document => "\uE8A5",
            FileCategory.Spreadsheet => "\uE9F9",
            FileCategory.Presentation => "\uE8FE",
            FileCategory.Code => "\uE943",
            FileCategory.Archive => "\uE7B8",
            FileCategory.Executable => "\uECAA",
            FileCategory.Database => "\uEE94",
            FileCategory.Font => "\uE8D2",
            FileCategory.System => "\uE9CE",
            _ => "\uE8A5"
        };
    }

    public static string DisplayName(FileCategory category)
    {
        return category switch
        {
            FileCategory.Folder => "Folder",
            FileCategory.Video => "Video",
            FileCategory.Image => "Image",
            FileCategory.Audio => "Audio",
            FileCategory.Document => "Document",
            FileCategory.Spreadsheet => "Spreadsheet",
            FileCategory.Presentation => "Presentation",
            FileCategory.Code => "Code",
            FileCategory.Archive => "Archive",
            FileCategory.Executable => "Executable",
            FileCategory.Database => "Database",
            FileCategory.Font => "Font",
            FileCategory.System => "System",
            _ => "Other"
        };
    }

    public static string DefaultColor(FileCategory category)
    {
        return category switch
        {
            FileCategory.Folder => "#3B82F6",
            FileCategory.Video => "#EF4444",
            FileCategory.Image => "#EC4899",
            FileCategory.Audio => "#A855F7",
            FileCategory.Document => "#0EA5E9",
            FileCategory.Spreadsheet => "#16A34A",
            FileCategory.Presentation => "#F97316",
            FileCategory.Code => "#8B5CF6",
            FileCategory.Archive => "#CA8A04",
            FileCategory.Executable => "#DC2626",
            FileCategory.Database => "#0891B2",
            FileCategory.Font => "#7C3AED",
            FileCategory.System => "#64748B",
            _ => "#94A3B8"
        };
    }
}
