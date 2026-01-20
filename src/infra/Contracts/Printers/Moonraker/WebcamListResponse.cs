using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Webcam Models
public class WebcamListResponse
{
    [JsonPropertyName("webcams")]
    public WebcamInfo[] Webcams { get; set; } = Array.Empty<WebcamInfo>();
}
