//
//  NTAG215Tag+getBlankTag.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/21/23.
//  Copyright © 2023 Daniel Radtke. All rights reserved.
//

import Foundation

extension TagDump {
    static func getBlankTag() -> TagDump {
        var tagData = Data(count: 572)
        tagData[0...8] = NTAG215Tag.getRandomUID()
        tagData[9...18] = Data([0x48, 0x00, 0x00, 0xE1, 0x10, 0x3E, 0x00, 0x03, 0x00, 0xFE])
        tagData[523...529] = Data([0xBD, 0x04, 0x00, 0x00, 0xFF, 0x00, 0x05])
        
        return TagDump(data: Data(tagData))!
    }
}
