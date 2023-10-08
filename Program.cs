using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.CommandLine;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")] internal sealed class Program
{
    unsafe record BitmapLockMetadata(int width, int height, nint scanPointer, int bypp, int stride)
    {
        public byte* scan0 => (byte*)scanPointer; // A pointer cannot be a record field so we do this hack
    }
    enum ColorFormatting{RGB, HSV, HEX}
    const int MAX_COLORDIST = 195075;
    const string SAVE_EXT = ".png";

    static RootCommand CreateRootCommand()
    {
        RootCommand rootCommand = new("EPFI - Extract pallette From Image");
        
        Argument<FileInfo> arg_path = new("path", "The path to the image that will have its pallette extracted");
        Argument<int> arg_colors = new("pallette-size", "The amount of colors the pallette will have. It cannot exceed the total colors in the image");
        Option<int> opt_tolerance = new("--similarity-tolerance", () => 50, $"How different 2 colors must be to be part of the pallette, bigger numbers are more restrictive. Ranges from 0 to {MAX_COLORDIST}");

        opt_tolerance.AddAlias("-t");


        Command cmd_outputMode1 = new("text", "Outputs the color pallette as a set of color codes");

        Option<ColorFormatting> opt_colorcodeF = new("--formatting-mode", () => ColorFormatting.RGB, "Specifies in which format the color codes will be printed");
        opt_colorcodeF.AddAlias("-m");

        cmd_outputMode1.Add(arg_path);
        cmd_outputMode1.Add(arg_colors);
        cmd_outputMode1.Add(opt_colorcodeF);

        cmd_outputMode1.SetHandler(Handler_TextOutput, arg_path, arg_colors, opt_tolerance, opt_colorcodeF);


        Command cmd_outputMode2 = new("file", $"Outputs the color pallette to a {SAVE_EXT} file");

        Option<FileInfo> opt_savePath = new("--output-path", () => new($"output{SAVE_EXT}"), "Specifies where the pallette will be saved");
        Option<int> opt_x = new("--stripe-width", () => 50, "Specifies the pixel width of each of the vertical color bands that will form the pallette");
        Option<int> opt_y = new("--height", () => 100, "Specifies the pixel height of the image the pallette will be saved to");

        opt_savePath.AddAlias("-o");
        opt_x.AddAlias("-w");
        opt_y.AddAlias("-h");

        cmd_outputMode2.Add(arg_path);
        cmd_outputMode2.Add(arg_colors);
        cmd_outputMode2.Add(opt_savePath);
        cmd_outputMode2.Add(opt_x);
        cmd_outputMode2.Add(opt_y);

        cmd_outputMode2.SetHandler(Handler_ImageOutput, arg_path, arg_colors, opt_tolerance, opt_savePath, opt_x, opt_y);
         
        rootCommand.Add(cmd_outputMode1);
        rootCommand.Add(cmd_outputMode2);
        rootCommand.AddGlobalOption(opt_tolerance);

        return rootCommand;
    }
    
    static Color[] GetSimplifiedpallette(Bitmap source, int palletteSize, int cleanupStrength)
    {        
        Color[] GetColorsInImage(Bitmap image)
        { 
            Dictionary<Color, int> colorFrequencies = new();
            BitmapData bitmapData = image.LockBits(new(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, image.PixelFormat);
            BitmapLockMetadata lockData = new(image.Width, image.Height, bitmapData.Scan0, Bitmap.GetPixelFormatSize(image.PixelFormat) / 8, bitmapData.Stride);

            // we cannot do parallelism on this section because for some unknown reason it is ever so slightly inconsistent and we fucking hate that
            unsafe
            {
                for(int x = 0; x < image.Width; x++) for(int y = 0; y < image.Height; y++)
                {
                    int offset = x * lockData.bypp + y * lockData.stride;
                    // mod 4 is a bit of a hack to check if the image supports transparency by having the bytes per pixel be divisible by 4
                    if(lockData.bypp % 4 == 0 && lockData.scan0[offset + 3] < 255) continue;
                    Color readColor = Color.FromArgb
                    (
                        lockData.scan0[offset + 2],
                        lockData.scan0[offset + 1],
                        lockData.scan0[offset]
                    );
                    if(colorFrequencies.ContainsKey(readColor)) colorFrequencies[readColor]++;
                    else colorFrequencies.Add(readColor, 0);
                }
            }

            image.UnlockBits(bitmapData);
            return colorFrequencies.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key).Distinct().ToArray();
        }
        
        double CalculateColorEucledianDistance(Color c1, Color c2) // max dist is 195075
        {
            double 
                rDifference = c1.R - c2.R,
                gDifference = c1.G - c2.G,
                bDifference = c1.B - c2.B;

                return Math.Sqrt(rDifference * rDifference + gDifference * gDifference + bDifference * bDifference);
        }

        Color[] CleanSimilarColorsFromList(Color[] colors, float tolerance)
        {
            int length = colors.Length;
            List<Color?> output = new(colors.Select(c => (Color?)c));

            for (int i = 0; i < length; i++) for (int j = 0; j < length; j++)
            {
                if(output[i] is null) break;
                if(i == j || output[j] is null) continue;

                if(CalculateColorEucledianDistance(colors[i], colors[j]) < tolerance) output[j] = null;
            }
            return output.Where(c => c is not null).Select(c => (Color)c!).ToArray();
        }
        
        if(palletteSize == 0) Crash("Cannot create a pallette of size 0.");

        Color[] fullpallette = GetColorsInImage(source);

        if(palletteSize > fullpallette.Length) palletteSize = fullpallette.Length;

        Color[] cleanedpallette = CleanSimilarColorsFromList(fullpallette, cleanupStrength);
        if(cleanedpallette.Length < palletteSize) for(int i = cleanupStrength; i == 0 || cleanedpallette.Length > palletteSize; i--) cleanedpallette = CleanSimilarColorsFromList(fullpallette, i);
        
        return cleanedpallette.Take(palletteSize).OrderBy(c => c.GetHue()).ToArray();
    }
  
    static Bitmap CreateStripedImage(int stripeWidth, int height, params Color[] stripeColors)
    {
        unsafe void CreateColoredStripe(int width, int xOffset, Color color, BitmapData bitmapData, BitmapLockMetadata bitmapMetadata)
        {
            for(int x = 0; x < width; x++) for(int y = 0; y < bitmapMetadata.height; y++)
            {
                int offset = (x + xOffset) * bitmapMetadata.bypp + y * bitmapMetadata.stride;

                bitmapMetadata.scan0[offset] = color.B;
                bitmapMetadata.scan0[offset + 1] = color.G;
                bitmapMetadata.scan0[offset + 2] = color.R;
                bitmapMetadata.scan0[offset + 3] = color.A;
            }
        }
        
        if(stripeColors.Length == 0) throw new ArgumentException("There must be at least one color when creating a striped image.");
        
        int width = stripeWidth * stripeColors.Length;
        
        Bitmap image = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData imageData = image.LockBits(new(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);
        BitmapLockMetadata lockData = new(width, height, imageData.Scan0, Bitmap.GetPixelFormatSize(image.PixelFormat) / 8, imageData.Stride);

        for(int i = 0; i < stripeColors.Length; i++) Parallel.For(stripeWidth * i, stripeWidth * (i + 1), j => CreateColoredStripe(1, j, stripeColors[i], imageData, lockData));

        image.UnlockBits(imageData);
        return image;
    }
    
    static void Handler_TextOutput(FileInfo read, int palletteSize, int dissimilarity, ColorFormatting formatting)
    {
        (double h, double s, double v) ColorToHSV(Color color)
        {
            int max = Math.Max(color.R, Math.Max(color.G, color.B));
            int min = Math.Min(color.R, Math.Min(color.G, color.B));
            
            return (Math.Round(color.GetHue(), 2), Math.Round((max == 0) ? 0 : 1d - (1d * min / max), 2), Math.Round(max / 255d, 2));
        }

        string ColorToHEX(Color color) => $"#{color.R.ToString("X2")}{color.G.ToString("X2")}{color.B.ToString("X2")}";
        
        if(!read.Exists) Crash("File specified does not exist");

        using FileStream fs = read.OpenRead(); 
        using Bitmap loadedImage = new(fs);

        Color[] pallette = GetSimplifiedpallette(loadedImage, palletteSize, dissimilarity);
        StringBuilder result = new();

        foreach(Color color in pallette) switch(formatting)
        {
            case ColorFormatting.RGB: result.Append($"R: {color.R.ToString().PadLeft(3, '0')} | G: {color.G.ToString().PadLeft(3, '0')} | B: {color.B.ToString().PadLeft(3, '0')}\n"); break;

            case ColorFormatting.HSV: 
                var hsvColor = ColorToHSV(color);
                result.Append($"H: {hsvColor.h.ToString().PadLeft(6, '0')} | S: {hsvColor.s.ToString().PadLeft(6, '0')} | V: {hsvColor.v.ToString().PadLeft(6, '0')}\n");
            break;

            case ColorFormatting.HEX: result.Append(ColorToHEX(color) + '\n'); break;
        }

        Console.Write(result.ToString());
    }

    static void Handler_ImageOutput(FileInfo read, int palletteSize, int dissimilarity, FileInfo save, int stripeWidth, int stripeHeight)
    {       
        string savePath = save.FullName;
        if(savePath.Contains('.')) savePath = savePath.Substring(0, savePath.LastIndexOf('.'));
        if(File.Exists(save.FullName))
        {
            int counter = 1;
            for(;File.Exists($"{savePath}({counter}){SAVE_EXT}"); counter++); // we wait
            savePath += $"({counter}){SAVE_EXT}";
        }
        else savePath += SAVE_EXT;
        
        if(!read.Exists) Crash("File specified does not exist");

        using FileStream fs = read.OpenRead(); 
        using Bitmap loadedImage = new(fs);

        Color[] pallette = GetSimplifiedpallette(loadedImage, palletteSize, dissimilarity);
        if(pallette.Length == 0) Crash("Unexpected error.");
        using Bitmap output = CreateStripedImage(stripeWidth, stripeHeight, pallette);
        output.Save(savePath, ImageFormat.Png);
    }

    static void Crash(object message)
    {
        ConsoleColor original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = original;
        Environment.Exit(0);
    }
    
    static async Task<int> Main(string[] args) => await CreateRootCommand().InvokeAsync(args);
}