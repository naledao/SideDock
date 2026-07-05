//
// Copyright (C) Microsoft Corporation. All rights reserved.
//

#pragma once
#ifndef VIRTUALCAMERA_MEDIASOURCE_H
#define VIRTUALCAMERA_MEDIASOURCE_H

#include <initguid.h>

// {951EE24C-E200-4E62-8035-F76214F695D2}
DEFINE_GUID(CLSID_VirtualCameraMediaSource,
    0x951ee24c, 0xe200, 0x4e62, 0x80, 0x35, 0xf7, 0x62, 0x14, 0xf6, 0x95, 0xd2);

static LPCWSTR VIRTUALCAMERAMEDIASOURCE_CLSID = L"{951EE24C-E200-4E62-8035-F76214F695D2}";
static LPCWSTR VIRTUALCAMERAMEDIASOURCE_FRIENDLYNAME = L"SideDock Camera Media Source";

// The below 2 GUIDs are defined in Windows build 22621 and above only, 
// if targetting SDK for Windows 22621 or higher this would need to be commented out.
// GUIDs for retrieving the device source instead of recreating it from within the vcam
//DEFINE_GUID(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES,
//    0xF0273718, 0x4A4D, 0x4AC5, 0xA1, 0x5D, 0x30, 0x5E, 0xB5, 0xE9, 0x06, 0x67);
//
//DEFINE_GUID(MF_VIRTUALCAMERA_ASSOCIATED_CAMERA_SOURCES,
//    0x1BB79E7C, 0x5D83, 0x438C, 0x94, 0xD8, 0xE5, 0xF0, 0xDF, 0x6D, 0x32, 0x79);

// --> VirtualCameraMediaSource activation attributes:

// {3C31A5F8-2795-4FB9-A0A1-C733A65C0CE8}
// The value of this attribute is the physical camera symboliclink name that the VirtualCameraMediaSource
// will be using.
DEFINE_GUID(VCAM_DEVICE_INFO,
    0x3c31a5f8, 0x2795, 0x4fb9, 0xa0, 0xa1, 0xc7, 0x33, 0xa6, 0x5c, 0xc, 0xe8);

// {C7F7C57B-DF30-41D0-AFFC-15201CDF920D}
// Defines the kind of virtual camera to be instantiated
DEFINE_GUID(VCAM_KIND,
    0xc7f7c57b, 0xdf30, 0x41d0, 0xaf, 0xfc, 0x15, 0x20, 0x1c, 0xdf, 0x92, 0xd);

// <-- VirtualCameraMediaSource activation attributes

#endif
