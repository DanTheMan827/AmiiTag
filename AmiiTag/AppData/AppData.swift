//
//  AppDataProtocol.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 4/6/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

class AppData {
    var RawData: Data
    
    required init?(_ data: Data) {
        if data.count != 216 {
            return nil
        }
        
        self.RawData = Data(data)
    }
}
