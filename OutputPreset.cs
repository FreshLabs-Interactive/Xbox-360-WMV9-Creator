using System;
using System.Linq;

namespace XboxWMVCreator
{
    /// <summary>
    /// One selectable output setting: frame size, frame rate and the video bitrate that goes with them.
    /// The text in the dropdown is the preset's Label (see ToString).
    /// </summary>
    internal sealed class OutputPreset
    {
        public string Id { get; }
        public string Label { get; }
        public int Width { get; }
        public int Height { get; }
        public int FrameRate { get; }

        /// <summary>WMV9 video bitrate in bits per second.</summary>
        public uint VideoBitrate { get; }

        private OutputPreset(string id, string label, int width, int height, int frameRate, uint videoBitrate)
        {
            Id = id;
            Label = label;
            Width = width;
            Height = height;
            FrameRate = frameRate;
            VideoBitrate = videoBitrate;
        }

        // Highest quality first. Edit this table to add or tweak presets; nothing else needs to change.
        //
        // The 720p30 (5 Mbps) and 1080p30 (~7.8 Mbps) bitrates come from WMV files known to play on the
        // console. The 60 fps and 480p bitrates are scaled from those and are untested: none of the known-good
        // files actually runs at 60 fps.
        public static readonly OutputPreset[] All =
        {
            new OutputPreset("1080p60", "1080p @ 60 fps (1920x1080)", 1920, 1080, 60, 12_000_000),
            new OutputPreset("1080p30", "1080p @ 30 fps (1920x1080)", 1920, 1080, 30,  8_000_000),
            new OutputPreset("720p60",  "720p @ 60 fps (1280x720)",   1280,  720, 60,  6_000_000),
            new OutputPreset("720p30",  "720p @ 30 fps (1280x720)",   1280,  720, 30,  5_000_000),
            new OutputPreset("480p30",  "480p @ 30 fps (854x480)",     854,  480, 30,  1_500_000),
        };

        /// <summary>The most conservative common choice, used until the user picks something else.</summary>
        public static OutputPreset Default => All.First(p => p.Id == "720p30");

        /// <summary>Finds a preset by its saved Id; unknown or empty Ids fall back to the default.</summary>
        public static OutputPreset FromId(string? id) =>
            All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

        public override string ToString() => Label;
    }
}
