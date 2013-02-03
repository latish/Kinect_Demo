﻿using System;
using System.Collections.Generic;
using System.Media;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;
using System.Windows.Shapes;
using Microsoft.Kinect;
using Microsoft.Speech.Recognition;
using Utility;

namespace Kinect9.LightSaber
{
	public partial class MainWindow
	{
		private const int SabrePositionCount = 20;
		private const ColorImageFormat ColorFormat = ColorImageFormat.RgbResolution640x480Fps30;
		private Skeleton[] _skeletons;
		private List<double> _previousSabre1PositionX, _previousSabre2PositionX;
		private int _player1Strength, _player2Strength, _player1Wins, _player2Wins;
		private DateTime _player1HitTime, _player2HitTime;
		private bool _gameMode, _hulkMode;
		private KinectAudioSource _kinectAudioSource;
		private SpeechRecognitionEngine _speechRecognizer;

		public int Player1Strength
		{
			get { return _player1Strength; }
			set
			{
				if (value.Equals(_player1Strength)) return;
				_player1Strength = value;
				PropertyChanged.Raise(() => Player1Strength);
			}
		}

		public int Player2Strength
		{
			get { return _player2Strength; }
			set
			{
				if (value.Equals(_player2Strength)) return;
				_player2Strength = value;
				PropertyChanged.Raise(() => Player2Strength);
			}
		}

		public int Player1Wins
		{
			get { return _player1Wins; }
			set
			{
				if (value.Equals(_player1Wins)) return;
				_player1Wins = value;
				PropertyChanged.Raise(() => Player1Wins);
			}
		}

		public int Player2Wins
		{
			get { return _player2Wins; }
			set
			{
				if (value.Equals(_player2Wins)) return;
				_player2Wins = value;
				PropertyChanged.Raise(() => Player2Wins);
			}
		}

		public bool GameMode
		{
			get { return _gameMode; }
			set
			{
				if (value.Equals(_gameMode)) return;
				_gameMode = value;
				PropertyChanged.Raise(() => GameMode);
			}
		}

		public bool HulkMode
		{
			get { return _hulkMode; }
			set
			{
				if (value.Equals(_hulkMode)) return;
				_hulkMode = value;
				PropertyChanged.Raise(() => HulkMode);
			}
		}

		private void Initialize()
		{
			if (_kinectSensor == null)
				return;
			_kinectSensor.AllFramesReady += KinectSensorAllFramesReady;
			_kinectSensor.ColorStream.Enable();
			_kinectSensor.SkeletonStream.Enable(new TransformSmoothParameters
																{
																	Correction = 0.5f,
																	JitterRadius = 0.05f,
																	MaxDeviationRadius = 0.05f,
																	Prediction = 0.5f,
																	Smoothing = 0.5f
																});
			_speechRecognizer = CreateSpeechRecognizer();
			_speechRecognizer.SetInputToDefaultAudioDevice();
			_speechRecognizer.RecognizeAsync(RecognizeMode.Multiple);

			_previousSabre1PositionX = new List<double>();
			_previousSabre2PositionX = new List<double>();
			ResetPlayerStrength();
			Player1Wins = Player2Wins = 0;
			GameMode = false;
			HulkMode = false;

			_kinectSensor.Start();
			_kinectAudioSource = _kinectSensor.AudioSource;
			_kinectAudioSource.Start();
			Message = "Kinect connected";
		}

		private SpeechRecognitionEngine CreateSpeechRecognizer()
		{
			var recognizerInfo = GetKinectRecognizer();

			var speechRecognitionEngine = new SpeechRecognitionEngine(recognizerInfo.Id);

			var grammar = new Choices();
			grammar.Add("hulk");
			grammar.Add("smash");

			var gb = new GrammarBuilder { Culture = recognizerInfo.Culture };
			gb.Append(grammar);

			var g = new Grammar(gb);

			speechRecognitionEngine.LoadGrammar(g);
			speechRecognitionEngine.SpeechRecognized += SreSpeechRecognized;

			return speechRecognitionEngine;
		}

		private void SreSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
		{
			if (e.Result.Confidence < 0.4)
				return;

			switch (e.Result.Text.ToUpperInvariant())
			{
				case "HULK":
					HulkMode = true;
					break;
				case "SMASH":
					MessageTextBlock.Foreground = Brushes.Red;
					break;
			}
		}

		private static RecognizerInfo GetKinectRecognizer()
		{
			Func<RecognizerInfo, bool> matchingFunc = r =>
			{
				string value;
				r.AdditionalInfo.TryGetValue("Kinect", out value);
				return "True".Equals(value, StringComparison.InvariantCultureIgnoreCase) && "en-US".Equals(r.Culture.Name, StringComparison.InvariantCultureIgnoreCase);
			};
			return SpeechRecognitionEngine.InstalledRecognizers().Where(matchingFunc).FirstOrDefault();
		}

		void KinectSensorAllFramesReady(object sender, AllFramesReadyEventArgs e)
		{
			using (var frame = e.OpenColorImageFrame())
			{
				if (frame == null)
					return;

				var pixelData = new byte[frame.PixelDataLength];
				frame.CopyPixelDataTo(pixelData);
				if (ImageSource == null)
					ImageSource = new WriteableBitmap(frame.Width, frame.Height, 96, 96,
							PixelFormats.Bgr32, null);

				var stride = frame.Width * PixelFormats.Bgr32.BitsPerPixel / 8;
				ImageSource = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, pixelData, stride);
			}

			using (var frame = e.OpenSkeletonFrame())
			{
				if (frame == null)
					return;

				if (_skeletons == null)
					_skeletons = new Skeleton[frame.SkeletonArrayLength];

				frame.CopySkeletonDataTo(_skeletons);
			}

			var trackedSkeleton = _skeletons.Where(s => s.TrackingState == SkeletonTrackingState.Tracked).ToList();

			if (!trackedSkeleton.Any())
				return;

			//Assumptions: Player 1 on left side of screen with saber in right hand, Player 2 on right side of screen with saber in left hand

			DrawSaber(trackedSkeleton[0], Sabre1, FightingHand.Right, HulkMode);
			GameMode = false;
			if (trackedSkeleton.Count > 1)
			{
				GameMode = true;
				DrawSaber(trackedSkeleton[1], Sabre2, FightingHand.Left, false);
				DetectSaberCollision();
				DetectPlayerHit(trackedSkeleton[0], trackedSkeleton[1], Sabre1, Sabre2);
			}
		}

		private void DrawSaber(Skeleton skeleton, Line sabre, FightingHand fightingHand, bool inHulkMode)
		{
			Joint jointWrist, jointHand, jointElbow;

			switch (fightingHand)
			{
				case FightingHand.Left:
					jointWrist = skeleton.Joints[JointType.WristLeft];
					jointElbow = skeleton.Joints[JointType.ElbowLeft];
					jointHand = skeleton.Joints[JointType.HandLeft];
					break;
				case FightingHand.Right:
					jointWrist = skeleton.Joints[JointType.WristRight];
					jointElbow = skeleton.Joints[JointType.ElbowRight];
					jointHand = skeleton.Joints[JointType.HandRight];
					break;
				default:
					throw new ArgumentOutOfRangeException("fightingHand");
			}

			if ((jointWrist.TrackingState == JointTrackingState.NotTracked) ||
				(jointElbow.TrackingState == JointTrackingState.NotTracked) ||
				(jointHand.TrackingState == JointTrackingState.NotTracked))
				return;

			var mapper = new CoordinateMapper(_kinectSensor);

			var wrist = mapper.MapSkeletonPointToColorPoint(jointWrist.Position, ColorFormat);
			var elbow = mapper.MapSkeletonPointToColorPoint(jointElbow.Position, ColorFormat);
			var hand = mapper.MapSkeletonPointToColorPoint(jointHand.Position, ColorFormat);

			double handAngleInDegrees;
			if (elbow.X == wrist.X)
				handAngleInDegrees = 0;
			else
			{
				var handAngleInRadian = Math.Atan((double)(elbow.Y - wrist.Y) / (wrist.X - elbow.X));
				handAngleInDegrees = handAngleInRadian * 180 / Math.PI;
			}

			if ((fightingHand == FightingHand.Right && (wrist.X < elbow.X))
				 || (fightingHand == FightingHand.Left && (wrist.X < elbow.X || wrist.Y > elbow.Y)))
				handAngleInDegrees = 180 + handAngleInDegrees;
			//Message = string.Format("{0}, {1}, {2}", elbow.Y, wrist.Y, handAngleInDegrees.ToString());	

			const int magicFudgeNumber = 45;
			double rotationAngleOffsetInDegrees;
			switch (fightingHand)
			{
				case FightingHand.Left:
					rotationAngleOffsetInDegrees = handAngleInDegrees - magicFudgeNumber;
					break;
				case FightingHand.Right:
					rotationAngleOffsetInDegrees = handAngleInDegrees + magicFudgeNumber;
					break;
				default:
					throw new ArgumentOutOfRangeException("fightingHand");
			}
			var rotationAngleOffsetInRadians = rotationAngleOffsetInDegrees * Math.PI / 180;

			//All measurements scaled to twice the size
			sabre.X1 = 2 * ((double)wrist.X + hand.X) / 2;
			sabre.Y1 = 2 * ((double)wrist.Y + hand.Y) / 2;

			const int sabreLength = 350;
			sabre.X2 = sabre.X1 + sabreLength * Math.Cos(rotationAngleOffsetInRadians);
			sabre.Y2 = sabre.Y1 - sabreLength * Math.Sin(rotationAngleOffsetInRadians);

			PlaySabreSoundOnWave(sabre, fightingHand == FightingHand.Right ? _previousSabre1PositionX : _previousSabre2PositionX);

			if (inHulkMode)
			{
				//alredy have player 1 right hand info
				Canvas.SetLeft(RightHandImage,2*hand.X-RightHandImage.ActualWidth/2);
				Canvas.SetTop(RightHandImage,2*hand.Y-RightHandImage.ActualHeight/2);
				var anticlockwiseAngle = 360 - handAngleInDegrees;
				RightHandImage.RenderTransform = new RotateTransform( anticlockwiseAngle,RightHandImage.ActualWidth/2,RightHandImage.ActualHeight/2);

				var headJoint = skeleton.Joints[JointType.Head];
				if(headJoint.TrackingState!=JointTrackingState.NotTracked)
				{
					var head =mapper.MapSkeletonPointToColorPoint(headJoint.Position,ColorFormat);
					Canvas.SetLeft(HeadImage,2*head.X- HeadImage.ActualWidth/2);
					Canvas.SetTop(HeadImage,2*head.Y- HeadImage.ActualHeight/2);
				}
			}
		}

		private void PlaySabreSoundOnWave(Line sabre, List<double> previousPositions)
		{
			if (!previousPositions.Any())
			{
				previousPositions.Add(sabre.X2);
				return;
			}

			if (previousPositions.Count >= SabrePositionCount)
				previousPositions.RemoveAt(0);

			const int minimumDistanceForSoundEffect = 100;
			if (sabre.X2 < previousPositions.Last())
			{
				if (sabre.X2 < (previousPositions.Min() - minimumDistanceForSoundEffect))
					PlaySabreSound(previousPositions);
			}
			else
			{
				if (sabre.X2 > (previousPositions.Max() + minimumDistanceForSoundEffect))
					PlaySabreSound(previousPositions);
			}
			previousPositions.Add(sabre.X2);
		}

		private void PlaySabreSound(List<double> previousPositions)
		{
			var soundPlayer = new SoundPlayer(@"Resources\lightsabre.wav");
			soundPlayer.Play();
			previousPositions.Clear();
		}

		private void DetectSaberCollision()
		{
			if (Sabre1.X2 > Sabre2.X2 &&
				 ((Sabre1.Y2 > Sabre2.Y1 && Sabre1.Y2 < Sabre2.Y2) || (Sabre1.Y2 < Sabre2.Y1 && Sabre1.Y2 > Sabre2.Y2)))
			{
				var soundPlayer = new SoundPlayer(@"Resources\clash.wav");
				soundPlayer.Play();
				_previousSabre1PositionX.Clear();
				_previousSabre2PositionX.Clear();
			}
		}

		void ResetPlayerStrength()
		{
			Player1Strength = 5;
			Player2Strength = 5;
		}

		private void DetectPlayerHit(Skeleton skeleton1, Skeleton skeleton2, Line sabre1, Line sabre2)
		{
			var player1RightShoulder = skeleton1.Joints[JointType.ShoulderRight];
			var player1Head = skeleton1.Joints[JointType.Head];

			var player2LeftShoulder = skeleton2.Joints[JointType.ShoulderLeft];
			var player2Head = skeleton2.Joints[JointType.Head];

			if (player1Head.TrackingState == JointTrackingState.NotTracked || player1RightShoulder.TrackingState == JointTrackingState.NotTracked
				|| player2Head.TrackingState == JointTrackingState.NotTracked || player2LeftShoulder.TrackingState == JointTrackingState.NotTracked)
				return;

			var coordinateMapper = new CoordinateMapper(_kinectSensor);

			//player 1 got hit
			if (sabre2.X2 < 2 * coordinateMapper.MapSkeletonPointToColorPoint(player1RightShoulder.Position, ColorFormat).X
				 && sabre2.Y2 > 2 * coordinateMapper.MapSkeletonPointToColorPoint(player1Head.Position, ColorFormat).Y)
			{
				if (_player1HitTime.AddSeconds(1) < DateTime.Now)
				{
					Player1Strength--;
					_player1HitTime = DateTime.Now;
				}
			}

			//player 2 got hit
			if (sabre1.X2 > 2 * coordinateMapper.MapSkeletonPointToColorPoint(player2LeftShoulder.Position, ColorFormat).X
				 && sabre1.Y2 > 2 * coordinateMapper.MapSkeletonPointToColorPoint(player2Head.Position, ColorFormat).Y)
			{
				if (_player2HitTime.AddSeconds(1) < DateTime.Now)
				{
					Player2Strength--;
					_player2HitTime = DateTime.Now;
				}
			}

			if (Player1Strength <= 0 || Player2Strength <= 0)
			{
				if (Player1Strength > Player2Strength)
					Player1Wins++;
				else
					Player2Wins++;
				ResetPlayerStrength();
			}
		}
	}

	enum FightingHand
	{
		Left,
		Right
	}
}