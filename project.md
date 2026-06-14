I have recently bought an xteink x3


The reason I bought this was to enhance my work laptop with additional features over ble5.


Scenarios 
1. Dedicated mute button for teams
    1. I would like an app that handles teams mute and unmute, this would be for when I can no longer see the teams app. I would like to have an app running on my laptop that connects to the ereader. It would detect if the microphone is muted or not for the teams call and then send the equivalent of ctrl + alt + k to toggle the muting status. (I do not think that it will send the keyboard shortcut, rather call the win32 apis to do this). I want the ereader to show the status of the mute status and if the camera is active on the tablet. When the value changes it should do a partial screen refresh. 
1. Notes
    1. I will generate various notes and charts, and I want them to be synced to my device.

the host device is a windows laptop, I am concerned about auto reconnecting and low power usage.

I need to build the windows code in laptop_host/ and add the features to the existing crosspoint firmware for x3 in the current project