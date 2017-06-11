using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Torch;

namespace Concealment
{
    public class Settings : ViewModel
    {
        private bool _enabled = true;
        private double _concealDistance = 75000;
        private ulong _concealInterval = 3600;
        private double _revealDistance = 50000;
        private ulong _revealInterval = 60;

        public MTObservableCollection<string> ExcludedSubtypes { get; } = new MTObservableCollection<string>();

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        public ulong ConcealInterval
        {
            get => _concealInterval;
            set { _concealInterval = value; OnPropertyChanged(); }
        }

        public ulong RevealInterval
        {
            get => _revealInterval;
            set { _revealInterval = value; OnPropertyChanged(); }
        }

        public double ConcealDistance
        {
            get => _concealDistance;
            set { _concealDistance = value; OnPropertyChanged(); }
        }

        public double RevealDistance
        {
            get => _revealDistance;
            set { _revealDistance = value; OnPropertyChanged(); }
        }

        public void Save(string path)
        {
            var xmlSerializer = new XmlSerializer(typeof(Settings));
            using (var fileStream = File.Open(path, FileMode.OpenOrCreate))
            {
                xmlSerializer.Serialize(fileStream, this);
            }
        }

        public static Settings LoadOrCreate(string path)
        {
            if (!File.Exists(path))
                return new Settings();

            var xmlSerializer = new XmlSerializer(typeof(Settings));
            Settings result;
            using (var fileStream = File.OpenRead(path))
            {
                result = (Settings)xmlSerializer.Deserialize(fileStream);
            }
            return result;
        }
    }
}