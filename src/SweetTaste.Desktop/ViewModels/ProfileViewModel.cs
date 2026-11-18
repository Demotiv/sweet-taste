using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Профиль кондитера: название, имя, соцсети и логотип.</summary>
public sealed partial class ProfileViewModel : ViewModelBase
{
    private const int MaxSourceBytes = 20_000_000;
    private const int PreviewSide = 512;

    private readonly ProfileService _service;
    private readonly Action _onSaved;
    private byte[]? _photoBytes;

    public ProfileViewModel(ProfileService service, Action onSaved)
    {
        _service = service;
        _onSaved = onSaved;

        var profile = service.GetProfile();
        _name = profile.Name;
        _ownerName = profile.OwnerName;
        _instagram = profile.Instagram;
        _telegram = profile.Telegram;
        _website = profile.Website;
        _photoBytes = profile.Photo;
        _initials = BuildInitials(profile.OwnerName, profile.Name);
        _hasPhoto = profile.Photo is { Length: > 0 };

        if (_photoBytes is { Length: > 0 })
        {
            _photo = TryLoadBitmap(_photoBytes);
            _hasPhoto = _photo is not null;
        }
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _ownerName;

    [ObservableProperty]
    private string _instagram;

    [ObservableProperty]
    private string _telegram;

    [ObservableProperty]
    private string _website;

    [ObservableProperty]
    private Bitmap? _photo;

    [ObservableProperty]
    private bool _hasPhoto;

    [ObservableProperty]
    private string _initials = "К";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _savedMessage;

    [ObservableProperty]
    private bool _hasSavedMessage;

    [ObservableProperty]
    private string? _exportMessage;

    [ObservableProperty]
    private bool _hasExportMessage;

    [ObservableProperty]
    private string? _exportError;

    [ObservableProperty]
    private string _lastExportFolder = string.Empty;

    public bool CanOpenExportFolder => LastExportFolder.Length > 0;

    public void SetPhoto(byte[] raw)
    {
        if (raw.Length > MaxSourceBytes)
        {
            Error = "Файл слишком большой — выберите изображение до 20 МБ.";
            return;
        }

        try
        {
            using var input = new MemoryStream(raw);
            var source = new Bitmap(input);
            var width = source.PixelSize.Width;
            var height = source.PixelSize.Height;
            var scale = Math.Min(1.0, (double)PreviewSide / Math.Max(width, height));

            var preview = scale < 1.0
                ? source.CreateScaledBitmap(
                    new PixelSize(
                        Math.Max(1, (int)Math.Round(width * scale)),
                        Math.Max(1, (int)Math.Round(height * scale))),
                    BitmapInterpolationMode.HighQuality)
                : source;

            using var output = new MemoryStream();
            preview.Save(output);

            _photoBytes = output.ToArray();
            Photo = preview;
            HasPhoto = true;
            Error = null;
            SavedMessage = null;
            HasSavedMessage = false;
        }
        catch (Exception)
        {
            Error = "Не удалось прочитать изображение. Подойдёт PNG, JPG или WebP.";
        }
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        _photoBytes = null;
        Photo = null;
        HasPhoto = false;
        SavedMessage = null;
        HasSavedMessage = false;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _service.SaveProfile(Name, OwnerName, Instagram, Telegram, Website, _photoBytes);
            Error = null;
            SavedMessage = "Профиль сохранён";
            HasSavedMessage = true;
            _onSaved();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            SavedMessage = null;
            HasSavedMessage = false;
        }
    }

    /// <summary>Выгружает полную базу в выбранную папку и кладёт рядом инструкцию по переносу.</summary>
    public void ExportData(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            var path = Path.Combine(folderPath, $"sweet-taste-{stamp}.db");
            var index = 1;

            while (File.Exists(path))
            {
                path = Path.Combine(folderPath, $"sweet-taste-{stamp}-{index++}.db");
            }

            _service.ExportDatabase(path);
            WriteReadme(folderPath, Path.GetFileName(path));

            LastExportFolder = folderPath;
            ExportError = null;
            ExportMessage = "Данные выгружены: " + path;
            HasExportMessage = true;
            OnPropertyChanged(nameof(CanOpenExportFolder));
        }
        catch (Exception ex)
        {
            ExportMessage = null;
            HasExportMessage = false;
            ExportError = "Не удалось выгрузить данные: " + ex.Message;
        }
    }

    public void SetExportError(string message)
    {
        ExportMessage = null;
        HasExportMessage = false;
        ExportError = message;
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        if (LastExportFolder.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LastExportFolder}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            ExportError = "Не удалось открыть папку.";
        }
    }

    private static void WriteReadme(string folderPath, string databaseFileName)
    {
        var readme = $"""
            Sweet Taste — выгрузка данных
            Дата выгрузки: {DateTime.Now:dd.MM.yyyy HH:mm}
            Файл базы: {databaseFileName}

            Как перенести данные на другой компьютер:
            1. Закройте Sweet Taste на обоих компьютерах.
            2. На новом компьютере откройте папку %APPDATA%\SweetTaste\
            3. Скопируйте туда файл {databaseFileName} и переименуйте его в sweet-taste.db,
               заменив существующий файл (старую базу предварительно сохраните).
            4. Запустите Sweet Taste — все данные будут на месте.

            Файл .db — это база SQLite. Не открывайте её в блокноте и не редактируйте вручную.
            Выгрузка не зашифрована: храните её в надёжном месте.
            """;

        File.WriteAllText(Path.Combine(folderPath, "sweet-taste-README.txt"), readme);
    }

    partial void OnOwnerNameChanged(string value)
    {
        UpdateInitials();
        ClearSaved();
    }

    partial void OnNameChanged(string value)
    {
        UpdateInitials();
        ClearSaved();
    }

    partial void OnInstagramChanged(string value) => ClearSaved();

    partial void OnTelegramChanged(string value) => ClearSaved();

    partial void OnWebsiteChanged(string value) => ClearSaved();

    private void UpdateInitials()
    {
        Initials = BuildInitials(OwnerName, Name);
    }

    private void ClearSaved()
    {
        SavedMessage = null;
        HasSavedMessage = false;
    }

    private static string BuildInitials(string owner, string name)
    {
        var source = string.IsNullOrWhiteSpace(owner) ? name : owner;
        var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            0 => "К",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[1][..1]).ToUpperInvariant(),
        };
    }

    private static Bitmap? TryLoadBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
