//
//  NTAG215Tag+getRandomUID.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 5/16/22.
//  Copyright © 2022 Daniel Radtke. All rights reserved.
//

import Foundation

extension NTAG215Tag {
    static func getRandomUID() -> Data {
        var data = Data.getRandom(count: 9)
        data[0] = 0x04
        data[3] = data[0] ^ data[1] ^ data[2] ^ 0x88
        data[8] = data[4] ^ data[5] ^ data[6] ^ data[7]
        
        return data
    }
}
