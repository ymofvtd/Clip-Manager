param([string]$OutputFolder = (Join-Path $env:TEMP "ClipsManager-transcription-fixtures"))

$ErrorActionPreference = 'Stop'
Get-Command ffmpeg -ErrorAction Stop | Out-Null
$folder = (New-Item -ItemType Directory -Force -Path $OutputFolder).FullName
$sources = (New-Item -ItemType Directory -Force -Path (Join-Path $folder 'sources')).FullName

$kennedy = Join-Path $sources 'kennedy.wav'
$french = Join-Path $sources 'french.mp3'
$spanish = Join-Path $sources 'spanish.mp3'
$instrumental = Join-Path $sources 'carefree.mp3'
$emotes = Join-Path $sources 'emotes.wav'
# Whisper.net's English speech fixture and LibriVox public-domain readings.
Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/sandrohanea/whisper.net/1.9.1/tests/TestData/kennedy.wav' -OutFile $kennedy
Invoke-WebRequest -UseBasicParsing 'https://archive.org/download/les_miserables_tome_1_di_0910_librivox/lesmiserables_t1_01_hugo_64kb.mp3' -OutFile $french
Invoke-WebRequest -UseBasicParsing 'https://archive.org/download/losmiserables_1612_librivox/miserables_01_hugo_64kb.mp3' -OutFile $spanish
Invoke-WebRequest -UseBasicParsing 'https://incompetech.com/music/royalty-free/mp3-royaltyfree/Carefree.mp3' -OutFile $instrumental

Add-Type -AssemblyName System.Speech
$voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $voice.SetOutputToWaveFile($emotes)
    $voice.Speak('That last round was Pog Champ. Chat said Kappa when I tripped. We need more copium today. Pog, pog, pog.')
} finally {
    $voice.Dispose()
}

$common = @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi')
$mp4 = @('-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-threads', '2', '-c:a', 'aac', '-ar', '48000', '-ac', '2', '-b:a', '128k')
$chord = '0.045*sin(2*PI*220*t)+0.04*sin(2*PI*261.63*t)+0.034*sin(2*PI*329.63*t)+0.023*sin(2*PI*440*t)'

function Make-Fixture([string]$Name, [string[]]$Inputs) {
    & ffmpeg @common @Inputs @mp4 (Join-Path $folder "$Name.mp4")
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg failed creating $Name.mp4 (exit $LASTEXITCODE)." }
}

Make-Fixture 'english' @('-i', 'color=s=160x90:r=10', '-i', $kennedy, '-shortest')
$mix = '[1:a]aresample=16000,atrim=duration=11,adelay=15000:all=1[e];[2:a]aresample=16000,adelay=33000:all=1[f];[3:a]volume=0.6,atrim=duration=40[m];[1:a]aresample=16000,atrim=start=11:end=20,asetpts=PTS-STARTPTS,adelay=62000:all=1[e2];[e][f][m][e2]amix=inputs=4:normalize=0[a]'
Make-Fixture 'mixed' @('-i', 'color=s=160x90:r=10', '-i', $kennedy, '-ss', '0', '-t', '7', '-i', $french,
    '-f', 'lavfi', '-i', "aevalsrc=${chord}:s=16000:d=74", '-filter_complex', $mix, '-map', '0:v', '-map', '[a]', '-shortest')
Make-Fixture 'foreign' @('-i', 'color=s=160x90:r=10', '-ss', '8', '-t', '16', '-i', $spanish, '-shortest')
Make-Fixture 'music' @('-i', 'color=s=160x90:r=5:d=64', '-t', '64', '-i', $instrumental,
    '-metadata', 'comment=Excerpt of Carefree by Kevin MacLeod (incompetech.com), CC BY 4.0 https://creativecommons.org/licenses/by/4.0/')
Make-Fixture 'silent' @('-i', 'color=s=160x90:r=5:d=62', '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo:d=62')
Make-Fixture 'no-audio' @('-i', 'color=s=160x90:r=5:d=2')
Make-Fixture 'short-audio' @('-i', 'color=s=160x90:r=10:d=18', '-t', '2', '-i', $kennedy)
Make-Fixture 'delayed' @('-i', 'color=s=160x90:r=10:d=14', '-itsoffset', '6', '-t', '8', '-i', $kennedy)
Make-Fixture 'emotes' @('-i', 'color=s=160x90:r=10', '-i', $emotes, '-shortest')

# The second track is audio. Change its declared duration from 2 to 17 seconds without adding samples.
$bytes = [IO.File]::ReadAllBytes((Join-Path $folder 'short-audio.mp4'))
$labels = [Text.Encoding]::ASCII.GetString($bytes)
$movieHeader = $labels.LastIndexOf('mvhd')
$audioHeader = $labels.LastIndexOf('tkhd')
if ($movieHeader -lt 0 -or $audioHeader -lt 0 -or $bytes[$movieHeader + 4] -ne 0 -or $bytes[$audioHeader + 4] -ne 0) {
    throw 'Unexpected FFmpeg MP4 header layout.'
}
[byte[]]$timescale = $bytes[($movieHeader + 16)..($movieHeader + 19)]
[Array]::Reverse($timescale)
$declaredDuration = [BitConverter]::GetBytes([uint32](17 * [BitConverter]::ToUInt32($timescale, 0)))
[Array]::Reverse($declaredDuration)
[Array]::Copy($declaredDuration, 0, $bytes, $audioHeader + 24, 4)
[IO.File]::WriteAllBytes((Join-Path $folder 'truncated-audio.mp4'), $bytes)
Write-Output $folder
