//
//  Error+getAlertController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 2/16/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

extension Error {
    func getAlertController() -> UIAlertController {
        let alert = UIAlertController(title: "Oh no!", message: self.localizedDescription, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
        
        return alert
    }
}
