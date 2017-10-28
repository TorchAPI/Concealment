using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using Torch;
using Torch.Collections;

namespace Concealment
{
    public class Settings : ViewModel
    {
        private bool _enabled = true;
        private double _concealDistance = 75000;
        private int _concealInterval = 3600;
        private double _revealDistance = 50000;
        private int _revealInterval = 60;
        private bool _concealProduction;
        private bool _concealPirates;

        public MtObservableList<string> ExcludedSubtypes { get; } = new MtObservableList<string>();

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        public int ConcealInterval
        {
            get => _concealInterval;
            set { _concealInterval = value; OnPropertyChanged(); }
        }

        public int RevealInterval
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

        public bool ConcealProduction
        {
            get => _concealProduction;
            set { _concealProduction = value; OnPropertyChanged(); }
        }

        public bool ConcealPirates
        {
            get => _concealPirates;
            set { _concealPirates = value; OnPropertyChanged(); }
        }
    }
}